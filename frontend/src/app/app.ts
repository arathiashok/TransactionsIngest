import { DatePipe, DecimalPipe, NgTemplateOutlet } from '@angular/common';
import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Component, DestroyRef, ElementRef, OnInit, ViewChild, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { EMPTY, Subscription, catchError, finalize, of, switchMap, tap } from 'rxjs';

interface TransactionRow {
  sourceSystem: string;
  transactionId: number;
  cardNumber: string | null;
  locationCode: string;
  productName: string;
  amount: number;
  transactionTime: string;
  status: 'Active' | 'Revoked';
}

interface AuditEvent {
  id: number;
  sourceSystem: string;
  transactionId: number;
  action: 'Created' | 'Updated' | 'Revoked' | 'Reactivated';
  fieldName: string | null;
  oldValue: string | null;
  newValue: string | null;
  changedAtUtc: string;
}

interface ImportCounts {
  inserted: number;
  updated: number;
  revoked: number;
  reactivated: number;
  unchanged: number;
}

interface SourceImportResult extends ImportCounts {
  sourceSystem: string;
  status: 'Completed' | 'SkippedIncomplete' | 'SkippedInvalid' | 'RetrievalFailed' | 'DatabaseFailed' | 'NotProcessed';
  error: string | null;
}

interface ImportResult extends ImportCounts {
  referenceTimeUtc: string;
  isBusy: boolean;
  stoppedOnDatabaseFailure: boolean;
  sources: SourceImportResult[];
}

@Component({
  imports: [DatePipe, DecimalPipe, NgTemplateOutlet],
  selector: 'app-root',
  styleUrl: './app.css',
  templateUrl: './app.html',
})
export class App implements OnInit {
  private readonly http = inject(HttpClient);
  private readonly destroyRef = inject(DestroyRef);
  @ViewChild('transactionDialog') private transactionDialog!: ElementRef<HTMLDialogElement>;
  private globalAuditRequest?: Subscription;
  private transactionAuditRequest?: Subscription;
  private detailsOpener: HTMLButtonElement | null = null;

  protected readonly showingAudits = signal(false);
  protected readonly globalAudits = signal<AuditEvent[]>([]);
  protected readonly globalAuditsLoading = signal(false);
  protected readonly globalAuditsError = signal('');
  protected readonly selectedTransaction = signal<TransactionRow | null>(null);
  protected readonly transactionAudits = signal<AuditEvent[]>([]);
  protected readonly transactionAuditsLoading = signal(false);
  protected readonly transactionAuditsError = signal('');

  protected readonly transactions = signal<TransactionRow[]>([]);
  protected readonly loading = signal(false);
  protected readonly importing = signal(false);
  protected readonly loadError = signal('');
  protected readonly importError = signal('');
  protected readonly importSummary = signal<ImportResult | null>(null);
  protected readonly busy = computed(() => this.loading() || this.importing());
  protected readonly sourceFilter = signal('');
  protected readonly statusFilter = signal('');
  protected readonly sortOrder = signal('time-desc');
  protected readonly activeCount = computed(() =>
    this.transactions().filter(transaction => transaction.status === 'Active').length);
  protected readonly revokedCount = computed(() =>
    this.transactions().filter(transaction => transaction.status === 'Revoked').length);
  protected readonly sourceSystems = computed(() =>
    [...new Set(this.transactions().map(transaction => transaction.sourceSystem))]
      .sort((left, right) => left.localeCompare(right, 'en')));
  protected readonly filteredTransactions = computed(() => {
    const rows = this.transactions().filter(transaction =>
      (!this.sourceFilter() || transaction.sourceSystem === this.sourceFilter())
      && (!this.statusFilter() || transaction.status === this.statusFilter()));
    const sort = this.sortOrder();
    const direction = sort.endsWith('asc') ? 1 : -1;

    return rows.sort((left, right) => {
      const difference = sort.startsWith('amount')
        ? left.amount - right.amount
        : Date.parse(left.transactionTime) - Date.parse(right.transactionTime);
      return difference * direction
        || left.sourceSystem.localeCompare(right.sourceSystem, 'en')
        || left.transactionId - right.transactionId;
    });
  });
  protected readonly sourceIssues = computed(() =>
    this.importSummary()?.sources.filter(source => source.status !== 'Completed') ?? []);

  ngOnInit(): void {
    this.loadTransactions();
  }

  protected showAuditHistory(): void {
    if (this.busy()) return;
    this.showingAudits.set(true);
    this.loadGlobalAudits();
  }

  protected backToTransactions(): void {
    this.globalAuditRequest?.unsubscribe();
    this.showingAudits.set(false);
  }

  protected loadGlobalAudits(): void {
    this.globalAuditRequest?.unsubscribe();
    this.globalAudits.set([]);
    this.globalAuditsError.set('');
    this.globalAuditsLoading.set(true);
    this.globalAuditRequest = this.http.get<AuditEvent[]>('/api/audits').pipe(
      takeUntilDestroyed(this.destroyRef),
      finalize(() => this.globalAuditsLoading.set(false))
    ).subscribe({
      next: events => this.globalAudits.set(events),
      error: () => this.globalAuditsError.set('Could not load audit history. Check the API connection and try again.')
    });
  }

  protected openTransaction(transaction: TransactionRow, opener: HTMLButtonElement): void {
    if (this.busy()) return;
    this.selectedTransaction.set(transaction);
    this.detailsOpener = opener;
    this.loadTransactionAudits();
    if (!this.transactionDialog.nativeElement.open) this.transactionDialog.nativeElement.showModal();
  }

  protected closeTransaction(): void {
    this.transactionAuditRequest?.unsubscribe();
    this.transactionAudits.set([]);
    this.transactionAuditsError.set('');
    this.selectedTransaction.set(null);
    this.transactionDialog.nativeElement.close();
    this.detailsOpener?.focus();
    this.detailsOpener = null;
  }

  protected loadTransactionAudits(): void {
    const transaction = this.selectedTransaction();
    if (!transaction) return;
    // Cancelling the prior request prevents another selection's history appearing here.
    this.transactionAuditRequest?.unsubscribe();
    this.transactionAudits.set([]);
    this.transactionAuditsError.set('');
    this.transactionAuditsLoading.set(true);
    const source = encodeURIComponent(transaction.sourceSystem);
    this.transactionAuditRequest = this.http.get<AuditEvent[]>(
      `/api/transactions/${source}/${transaction.transactionId}/audits`
    ).pipe(
      takeUntilDestroyed(this.destroyRef),
      finalize(() => this.transactionAuditsLoading.set(false))
    ).subscribe({
      next: events => this.transactionAudits.set(events),
      error: (error: HttpErrorResponse) => this.transactionAuditsError.set(error.status === 404
        ? 'This transaction no longer exists. Close this panel and refresh the transactions.'
        : 'Could not load this transaction’s audit history. Please try again.')
    });
  }

  protected auditValue(value: string | null): string {
    return value === null ? '—' : value === '' ? '(empty)' : value;
  }

  protected loadTransactions(): void {
    if (this.busy()) return;
    this.loading.set(true);
    this.loadError.set('');

    this.fetchTransactions().pipe(
      takeUntilDestroyed(this.destroyRef),
      finalize(() => this.loading.set(false))
    ).subscribe();
  }

  protected importTransactions(): void {
    if (this.busy()) return;
    this.importing.set(true);
    this.importError.set('');
    this.loadError.set('');
    this.importSummary.set(null);

    this.http.post<ImportResult>('/api/import', null).pipe(
      catchError((error: HttpErrorResponse) => {
        this.importError.set(error.status === 409
          ? 'Another import is already running. Please wait and try again.'
          : error.status === 500
            ? 'The import stopped because of a server error. Some sources may already have been saved.'
            : 'The import request could not be completed. Check the API connection and try again.');

        const result = error.error as Partial<ImportResult> | null;
        return of(result?.stoppedOnDatabaseFailure === true && Array.isArray(result.sources)
          ? result as ImportResult : null);
      }),
      switchMap(result => {
        if (result && !result.isBusy) this.importSummary.set(result);
        // Refresh after errors too: earlier sources may already have committed changes.
        return this.fetchTransactions();
      }),
      takeUntilDestroyed(this.destroyRef),
      finalize(() => this.importing.set(false))
    ).subscribe();
  }

  private fetchTransactions() {
    return this.http.get<TransactionRow[]>('/api/transactions').pipe(
      tap(transactions => this.transactions.set(transactions)),
      catchError((_error: HttpErrorResponse) => {
        this.loadError.set('Could not load transactions. Check the API connection and try again.');
        return EMPTY;
      })
    );
  }
}
