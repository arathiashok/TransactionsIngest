import { DatePipe, DecimalPipe } from '@angular/common';
import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Component, DestroyRef, OnInit, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { EMPTY, catchError, finalize, of, switchMap, tap } from 'rxjs';

interface TransactionRow {
  sourceSystem: string;
  transactionId: number;
  locationCode: string;
  productName: string;
  amount: number;
  transactionTime: string;
  status: 'Active' | 'Revoked';
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
  imports: [DatePipe, DecimalPipe],
  selector: 'app-root',
  styleUrl: './app.css',
  templateUrl: './app.html',
})
export class App implements OnInit {
  private readonly http = inject(HttpClient);
  private readonly destroyRef = inject(DestroyRef);

  protected readonly transactions = signal<TransactionRow[]>([]);
  protected readonly loading = signal(false);
  protected readonly importing = signal(false);
  protected readonly loadError = signal('');
  protected readonly importError = signal('');
  protected readonly importSummary = signal<ImportResult | null>(null);
  protected readonly busy = computed(() => this.loading() || this.importing());
  protected readonly sourceIssues = computed(() =>
    this.importSummary()?.sources.filter(source => source.status !== 'Completed') ?? []);

  ngOnInit(): void {
    this.loadTransactions();
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
