# AI Usage and Development Notes

## How I used AI

I organized Transactions Ingest around the core engineering requirements: correct multi-source reconciliation, field-level auditability, safe handling of source failures, and a frontend where those results can be inspected. I specified the composite transaction identity, revocation conditions, audit conventions, and implementation scope, then reviewed each layer before building on it. I used ChatGPT to examine requirements, challenge assumptions, and refine some implementation prompts. Codex generated and revised substantial portions of the C# backend, Angular frontend, EF Core mappings and migrations, automated tests, and documentation.

I deliberately developed the application in phases. Each phase had a clear objective and a review point, so I could examine the code, run the relevant checks, and understand its behavior before integrating the next component. When the results did not match my expectations, I investigated the data and execution flow and used those findings to direct targeted revisions. The 24-hour revocation test is one example: checking the SQLite audit records led me to identify a mismatch between fixture timestamps and the live clock, then introduce a controlled testing setup without changing the business rule.

AI accelerated implementation, but I remained responsible for the technical direction, evaluating trade-offs, checking the outputs, and validating the final application. The code generation and many revisions were AI-assisted, as documented below.

## Development phases and key prompts

### Phase 1: Backend setup

I established the backend foundation first: an ASP.NET Core 10 Web API using controllers, a clean solution structure, and no premature database or business-logic layers. I handled the local environment and Git setup separately so the initial phase stayed focused on a working, understandable API scaffold.

<details>
<summary>Original prompt excerpts</summary>

```text
Let's start setting up the backend for my Transaction Ingest coding exercise.

I'm handling the development environment and Git repository setup myself.

Please create an ASP.NET Core 10 Web API project using controllers (not Minimal APIs) under `backend/TransactionIngest.Api`, with a solution file at the repository root.

Keep the project minimal and follow standard ASP.NET Core conventions. Remove unnecessary template example code such as WeatherForecast, and don't add any business logic, database integration, frontend, tests, or additional architectural layers yet.

Create a suitable root `.gitignore` for a .NET, Angular, and SQLite application. Don't initialize Git or make any commits.

Make sure the API project builds successfully.
```

</details>

**Implementation and review:** Codex created the solution and API scaffold, removed template code, and added `.gitignore`. I checked the generated project structure and build result before accepting this foundation. The review also surfaced invalid local launch ports, which I flagged for correction in the next phase.

### Phase 2: Database and models

I defined the data contract before implementing reconciliation. Because IDs repeat across the three sources, I specified `(SourceSystem, TransactionId)` as the transaction identity. I also established field-level audit behavior, exact decimal handling through integer-cent storage in SQLite, and consistent UTC timestamps. Those decisions gave the import service a predictable schema and clear rules to enforce.

<details>
<summary>Original prompt excerpts</summary>

Database foundation:

```text
### 1. Transaction entity

Include the following properties:

- SourceSystem (string)
- TransactionId (int)
- CardNumber (string)
- LocationCode (string)
- ProductName (string)
- Amount (decimal)
- TransactionTime (DateTime, UTC)
- Status (Active or Revoked)

Use `(SourceSystem, TransactionId)` as the composite primary key. TransactionId must not be database-generated.

Limit SourceSystem, CardNumber, and LocationCode to 19 characters. Allow ProductName up to 100 characters as a documented exception to the exercise's inconsistent sample data.

CardNumber can be empty for non-card payments.

Store Amount as an exact integer number of cents in SQLite while keeping its C# type as decimal. Enforce decimal(18,2) precision and range without silently rounding or truncating values. Do not restrict amounts to positive values or impose a positive-only constraint on TransactionId.

### 2. TransactionAudit entity

Include:

- Id (long, generated primary key)
- SourceSystem (string)
- TransactionId (int)
- Action (Created, Updated, Revoked, Reactivated)
- FieldName (nullable string)
- OldValue (nullable string)
- NewValue (nullable string)
- ChangedAtUtc (DateTime, UTC)

Configure a one-to-many relationship with Transaction using the composite foreign key and restrict deletion.

Store the enums as readable strings.

We've agreed on these audit conventions:

- One Created event for each new transaction, without separate entries for every initial field.
- One audit record per changed field during updates.
- Revocation and reactivation events should record Status changes.
- Repeated imports with identical data should not produce additional audit records.
- Chronological field-level history is sufficient; we don't need ChangeId.

These conventions will be implemented later in the import service. For now, only prepare the entity structure and mappings.

### 3. Database configuration

Create `TransactionIngestDbContext` under Data/ and configure the keys, relationships, conversions, and necessary constraints using EF Core Fluent API.

Ensure UTC timestamps are handled consistently when saving and reading data.

Add the required EF Core SQLite and design-time dependencies, register the DbContext in Program.cs, and configure the SQLite connection string in appsettings.json.

For the later reconciliation implementation, we've agreed that the 24-hour revocation window is based on TransactionTime and requires a successful, complete source snapshot. We don't need a LastSeenAtUtc field.
```

Configuration correction after code review:

```text
I've reviewed the database implementation, and the entities and EF Core mappings look good. Let's keep them unchanged.

Before generating the initial migration, I want to address two configuration issues.

1. In Program.cs, update the SQLite connection configuration so that a normal relative database filename is resolved against the application's content root rather than the process working directory. Use SqliteConnectionStringBuilder and preserve absolute paths and in-memory database configurations.
2. In Properties/launchSettings.json, replace the existing localhost:0 bindings with valid local development ports. Use HTTP 5100 and HTTPS 7100, provided those ports are available. Keep the existing launch profiles otherwise unchanged.
```

Migration generation instruction:

```text
1. Verify that the dotnet-ef CLI tool is available and compatible with EF Core 10. If it is missing, report that and stop before installing anything globally.
2. Generate an initial migration named InitialCreate for TransactionIngestDbContext using the existing API project. Place the migration in the standard Migrations/ directory within the API project.
3. Do not apply the migration or create the SQLite database yet.
```

Later authorization to apply the reviewed migration:

```text
The InitialCreate migration has been generated and reviewed. Let's apply it and verify the actual SQLite database.

We're working on feature/database-model.

Before applying the migration, confirm that the target is our local development database at the intended path under backend/TransactionIngest.Api. Inspect the generated migration for any unexpected destructive operations and stop if you find a discrepancy.
```

</details>

**Implementation and review:** Codex implemented the entities, EF Core mappings, SQLite configuration, and initial migration to the specified contract. Before applying the migration, I identified two configuration issues: database paths depended on the terminal's working directory, and the launch profiles used invalid ports. I directed both fixes, examined the migration, and then proceeded with schema and constraint verification.

### Phase 3: Mock source data

For the three required source systems, I chose a shared mock-source implementation with deterministic snapshots and explicit scenario selection. That kept source simulation separate from reconciliation and made it possible to repeat the same inputs during tests. I also distinguished a complete snapshot from incomplete data and retrieval failure, since those cases must not produce the same database behavior.

<details>
<summary>Original prompt excerpts</summary>

```text
Use an interface exposing SourceSystem and an asynchronous GetSnapshotAsync(CancellationToken) method.

SourceTransaction should be an immutable record containing the required incoming business fields, excluding SourceSystem, Status, and database-specific properties.

SourceSnapshot should contain the retrieved transactions and an IsComplete flag.

Use one MockTransactionSource implementation for all three source systems.

Support these explicitly selected scenarios:

- Baseline
- Changes
- Reactivation
- Incomplete
- Failure

Keep a small shared MockScenarioState and scenario enum in MockTransactionSource.cs rather than creating additional files. Default to Baseline.

Scenario selection must be explicit and stable. Fetching a snapshot must not automatically advance the scenario.

Use deterministic sample data. Repeated calls with the same scenario should return equivalent snapshots.

Include:

- Transaction IDs reused across different sources.
- An Online transaction with an empty CardNumber.
- Examples of new, updated, missing, and reappearing transactions.
- A delayed transaction with an older TransactionTime.

Keep source snapshots independent of EF Core entities and database access.

A successful complete snapshot, an incomplete snapshot, and a failed retrieval must remain distinguishable. An incomplete snapshot must not appear as an authoritative empty snapshot.

Register the three mock sources through dependency injection in Program.cs.
```

Payment-data correction:

```text
I'd like to make a small correction to the mock transaction data.

CardNumber should depend on the payment method, not the source system.

- For card payments, populate CardNumber with the masked last four digits, for example "\*\*\*4444".
- For non-card payments, such as cash, use an empty string "".
- Online transactions can also be card payments, so don't leave all Online CardNumber values empty.
```

Electronics fixture revision:

```text
The sample products are electronics and computer accessories, such as:

- Wireless Mouse
- Mechanical Keyboard
- USB-C Docking Station

Please update MockTransactionSource.cs to use products from the same category, such as headphones, USB-C adapters, keyboards, computer mice, monitors, and other computer accessories.

A few requirements:

1. Use different, realistic products and corresponding prices across Store Uptown, Store Downtown, and Online, rather than returning identical products for every source.
2. Include the three example products from the exercise where appropriate.
```

Final source-specific card correction:

```text
TransactionId and CardNumber should be independent. The same TransactionId across different sources does not imply the same payment card was used.

Please update MockTransactionSource.cs so each source has its own realistic masked card numbers.

For TransactionId 101, use:

- Store Uptown: "\*\*\*1111"
- Store Downtown: "\*\*\*0002"
- Online: "\*\*\*4444"

For the remaining transactions, use appropriate source-specific masked card numbers for card payments and empty strings "" for non-card payments.
```

</details>

**Implementation and review:** Codex implemented the source contract, immutable input records, completeness flag, and five mock scenarios. I then refined the fixtures to reflect realistic retail transactions: distinct electronics products and prices, masked card values for card payments, and empty card values for non-card payments. I also corrected the assumption that the same transaction ID across sources implied the same card. These changes made the test data more realistic without changing the reconciliation cases.

### Phase 4: Reconciliation and tests

Reconciliation was the core of the exercise, so I established its behavior before exposing any API. The service needed to match composite identities, update only fields that changed, preserve audit history, and remain idempotent. I specified the preceding 24-hour revocation interval and the requirement that only successful, complete snapshots can establish that a transaction is missing. I also required automated tests at this stage, before building controllers on top of the service.

<details>
<summary>Original prompt excerpts</summary>

Service implementation:

```text
### 1. Import Service

Create `Services/TransactionImportService.cs`.

The service should retrieve snapshots from the three registered ITransactionSource implementations and reconcile them against SQLite.

Process sources sequentially and independently.

For each source:
- Retrieve its snapshot.
- Validate the complete snapshot before making database changes.
- Load existing transactions belonging to that source.
- Match transactions using (SourceSystem, TransactionId).
- Apply the necessary changes.
- Save transaction updates and audit records atomically.

Keep the business logic in the service, not in controllers or mock source classes.

### 2. Reconciliation Rules

Implement these behaviors:

- New transaction: Insert as Active and create one Created audit event.
- Existing transaction with identical values: No database changes or new audit entries.
- Existing transaction with changed values: Update only changed fields and create one audit record per changed field.
- Missing transaction: Mark as Revoked only if it qualifies under the 24-hour rule.
- Previously revoked transaction reappears: Restore Active status and record reactivation, including any accompanying field changes.
- Repeated identical imports: No duplicate transactions or audit entries.

Compare CardNumber, LocationCode, ProductName, Amount, and TransactionTime.

SourceSystem and TransactionId are immutable identity fields.

### 3. Revocation Rule

Use the agreed interpretation of the coding exercise:

A missing Active transaction can be revoked only when its TransactionTime is within the preceding 24 hours of the import reference time.

Use the interval [reference time - 24 hours, reference time).

Only infer revocation from successful, complete snapshots.

Delayed transactions must still be accepted even when their TransactionTime is older than 24 hours.

Do not implement optional transaction finalization.

### 4. Validation and Error Handling

Validate the entire incoming snapshot before making changes.

Check required fields, permitted lengths, duplicate transaction IDs, UTC timestamps, and exact decimal(18,2) amounts.

For CardNumber, follow our agreed convention: an empty string for non-card payments or a masked value such as "***4444" for card payments. Do not store full card numbers.

Handle incomplete, invalid, and failed source snapshots without modifying that source's records.

Continue processing other sources after retrieval or validation failures. Stop processing if a database write fails, and propagate cancellation.

Use EF Core's atomic SaveChangesAsync behavior to persist each source's transaction changes and audit entries together.

### 5. Audit History

Follow our existing TransactionAudit model.

- Created: One event per new transaction.
- Updated: One event per changed business field.
- Revoked: Record Status changing from Active to Revoked.
- Reactivated: Record Status changing from Revoked to Active, along with any changed fields.

Preserve previous audit records.

Use invariant formatting for amounts and UTC timestamps in audit values.

### 6. Time and Concurrency

Inject TimeProvider into the service. Capture one UTC reference time per import rather than calling DateTime.UtcNow repeatedly.

Register TimeProvider.System for normal operation. Keep the service independent of the mock's fixed reference time so we can inject a controlled clock for automated tests and manual demonstrations later.

Prevent overlapping imports and scenario changes during an import using one shared application-wide synchronization mechanism.

Keep this minimal, and ensure MockScenarioState cannot bypass the synchronization mechanism through an unrestricted setter.

Do not add unnecessary synchronization frameworks or database tables.

### 7. Import Results

Create `Services/ImportResult.cs` for simple import summary records.

Report inserted, updated, revoked, reactivated, and unchanged transaction counts, along with per-source failures or skipped-source information.

Counts should represent transactions, not individual audit rows.
```

Separate automated-test request:

```text
Let's add automated tests for the TransactionImportService before implementing the API controllers.

Please use the current implementation on feature/transaction-import.

Create a minimal xUnit test project under tests/, using EF Core SQLite with an isolated database for each test. Use a controlled TimeProvider aligned with our mock reference time, rather than relying on the actual system clock.

Focus on verifying:

1. Baseline import inserts the expected transactions and Created audits.
2. Importing the same snapshot twice creates no duplicate transactions or audit records.
3. Changes updates existing transactions, records only changed fields, revokes eligible missing transactions, and inserts new and delayed transactions.
4. Reactivation restores previously revoked transactions and records the appropriate audit events.
5. The 24-hour revocation boundaries are correct, including exactly 24 hours old, older than 24 hours, and future-dated transactions.
6. Incomplete, invalid, and failed snapshots never cause unintended database changes.
7. Failures in one source do not prevent other valid sources from importing.
8. Concurrent imports are rejected, and scenarios cannot change during an active import.

Use independently configured test sources where necessary to verify source isolation. Avoid changing production mock fixtures just for testing.

Also verify that MockScenarioState is registered as a singleton in Program.cs.

Keep test helpers small and local to the test project. Don't add unnecessary production files, abstractions, or packages.

Run all tests and the solution build.
```

Constraint on fixing discovered failures:

```text
Do not automatically modify the production implementation to fix test failures. Report the issue so we can review the proposed fix first.
```

</details>

**Implementation and review:** Codex implemented `TransactionImportService` and import summaries. Reconciliation stays in the service; each complete snapshot is validated before processing, and transaction changes and audit events are committed atomically per source. I kept failure handling source-specific and required concurrency protection. The initial 38 passing backend tests covered lifecycle changes, idempotency, 24-hour boundaries, failed or incomplete sources, and concurrency. I also required test failures to be reported for review rather than allowing automatic changes to production logic simply to satisfy an assertion.

### Phase 5: Backend APIs

Once the reconciliation behavior was covered by tests, I added the API layer. My design choice was to keep controllers responsible for HTTP requests and responses, with all reconciliation rules remaining in the existing service. This avoided duplicating business logic between API endpoints and background components.

<details>
<summary>Original prompt excerpts</summary>

```text
**1. POST /api/import**

Create ImportController.cs.

- Inject and call the existing TransactionImportService.
- Return the import summary with per-source results.
- Return 200 OK for a completed import, including partial source retrieval failures reported in the summary.
- Return 409 Conflict when another import is already running.
- Return 500 with an appropriate response when a database failure stops the import.
- Pass the request CancellationToken to the service.
- Keep all reconciliation logic inside the existing service.

**2. GET /api/transactions**

Create TransactionsController.cs.

- Retrieve transactions from SQLite using TransactionIngestDbContext.
- Use AsNoTracking for this read-only operation.
- Return SourceSystem, TransactionId, LocationCode, ProductName, Amount, TransactionTime, and Status.
- Return Status as a readable string, such as "Active" or "Revoked".
- Use a consistent ordering by SourceSystem and TransactionId.
- Return an empty array if no transactions exist.
- Avoid exposing EF Core navigation properties.
```

</details>

**Implementation and review:** Codex added `POST /api/import` to trigger reconciliation and `GET /api/transactions` to retrieve persisted records. I checked the responses using live API requests, including repeat imports and failure cases, rather than relying only on compilation. A masked card field was added to the transaction response later, when the details view required it.

### Phase 6: Angular frontend

I kept the first Angular iteration deliberately small: a standalone application with typed HTTP calls, a transaction table, and a manual import action. I deferred routing, component libraries, and extra services until there was a demonstrated need. The priority was a working end-to-end connection to the API before investing in presentation.

<details>
<summary>Original prompt excerpts</summary>

Application setup:

```text
First, check the installed Node.js and npm versions and confirm they support the latest stable Angular release.

Set up a new Angular application inside the existing repository under `frontend/`.

Use:

- The latest stable compatible Angular version.
- Standalone components and TypeScript.
- Plain CSS.
- npm for package management.
- No routing or server-side rendering, since this is a single-page application.

Keep the project minimal. Use the standard Angular CLI setup, but don't add UI libraries, extra components, services, or unnecessary configuration files yet.

Replace the default Angular welcome screen with a simple page displaying "Transaction Ingest" as the heading. We'll implement the transaction table and backend integration in the next step.

Make sure the Angular project uses our existing Git repository rather than creating a nested Git repository. Check that generated build output and node_modules are ignored by Git.
```

Transaction interface and proxy:

```text
Let's implement the Angular transaction interface using the existing project under frontend/.

Keep the UI clean, minimal, and professional. No additional UI libraries or unnecessary components.

Requirements:

1. Display the transactions returned by GET /api/transactions in a table with Source System, Transaction ID, Location Code, Product Name, Amount, Transaction Time, and Status.
2. Fetch existing transactions when the page loads.
3. Add an "Import Transactions" button that calls POST /api/import and refreshes the table afterward.
4. Display a concise import summary using the counts returned by the API.
5. Handle loading, empty results, API errors, and import-in-progress states. Disable the import button while a request is running.
6. Show Active and Revoked statuses clearly. Format amounts to two decimal places and display transaction timestamps consistently.
7. Use Angular's HttpClient with typed responses and appropriate error handling.

For local development, configure a minimal Angular proxy for /api requests using the existing ASP.NET Core backend's local HTTP endpoint. Verify the backend's launch configuration and avoid changing backend files unless absolutely necessary.

Keep the implementation within the existing Angular root component and configuration files where practical. Add only the minimal proxy configuration required. No routing, state-management libraries, authentication, pagination, mock scenario controls, or additional features yet.
```

</details>

**Implementation and review:** Codex built the Angular client and configured the development proxy to the API. I exercised the application with the backend running, confirmed that imports were followed by fresh API reads, and checked empty states, repeat imports, errors, and recovery. I moved to visual enhancements only after that basic integration was working.

### Phase 7: Dashboard design and branding

With the end-to-end workflow in place, I defined the dashboard improvements: a consistent teal-and-white layout, summary counts, source and status filters, sorting, and independent Refresh and Import actions. I kept filtering and sorting client-side because the mock dataset is small and the existing APIs already provide the required data.

<details>
<summary>Original prompt excerpts</summary>

```text
I'd like to refine the Angular frontend into a more polished transaction management dashboard. The current functionality is working, so please focus on improving the UI and usability without changing the existing backend integration.

I'd prefer a clean, professional design with a teal-and-white theme, neutral backgrounds, consistent typography, and well-organized sections.

Here's the layout I have in mind:

**1. Application header**

- A full-width header with a simple application icon and "Transactions Ingest" branding.
- A clean, minimal appearance suitable for a retail operations dashboard.

**2. Dashboard heading and summary**

- A prominent "Transaction Management" heading with a short description.
- Three summary cards showing Total Transactions, Active Transactions, and Revoked Transactions.
- Calculate these values from the transactions retrieved through the existing API.

**3. Transaction management section**

- Keep the Import Transactions button and add a separate Refresh button.
- Provide Source System and Status dropdown filters, along with sorting by transaction time or amount.
- Display the number of matching records.
- Keep the seven required transaction columns, with clearly distinguished status badges.
- Use balanced spacing, subtle borders, readable typography, and a clean table layout.

**4. User experience**

- Preserve the existing import summary, including inserted, updated, revoked, reactivated, and unchanged counts.
- Maintain loading indicators, empty states, error messages, and Retry functionality.
- Keep buttons disabled while relevant requests are processing.
- Make the layout responsive and accessible.

Please preserve the existing GET /api/transactions and POST /api/import integration, including automatic data refresh after importing.

Use client-side filtering and sorting. Don't introduce new backend endpoints, libraries, routing, pagination, transaction detail pages, or audit history views.

Keep the implementation within the existing Angular files wherever practical. I don't want additional components or services unless genuinely necessary.
```

Branding follow-up:

```text
I'd like to replace the default Angular logo in our application header with a more appropriate icon for Transaction Ingest.
Use a simple transaction/exchange icon with two horizontal arrows, styled in our existing teal-and-white color scheme.
Keep it minimal and professional, aligned with the "Transactions Ingest" branding. Prefer a lightweight inline SVG rather than adding an icon library.
If the browser favicon still uses Angular branding, update that to match as well.
```

</details>

**Implementation and review:** Codex implemented the revised layout and controls while preserving the import workflow. I reviewed the rendered dashboard, replaced the default Angular branding with a transaction-themed icon and matching favicon, and verified the result in the browser. These were focused frontend changes with no new API or UI-library dependency.

### Phase 8: Manual reconciliation testing

Manual lifecycle testing exposed an important distinction between an implementation bug and a test-data issue. Transaction 102 disappeared from the `Changes` snapshot but remained Active. I traced that behavior through the SQLite audit records, fixture timestamps, and the import service's reference time. The transaction was already older than 24 hours, so the service had applied the rule correctly; the test setup needed adjustment.

<details>
<summary>Original prompt excerpts</summary>

```text
I want to manually test the transaction lifecycle through our running Angular application: Baseline → Changes → Reactivation.

Right now, the mock fixtures use a fixed reference time, but the import service uses the actual UTC time. This prevents me from reliably testing the 24-hour revocation rule.

Please review the existing Program.cs, MockScenarioState, MockTransactionSource, and TimeProvider registration.

Make a minimal change so that, in the Development environment only, I can:

- Choose the mock scenario using an environment variable.
- Override TimeProvider with a fixed UTC timestamp using another environment variable.
- Keep the existing default behavior when those variables aren't provided.

Use 2026-10-25T12:00:00Z as the reference time for our manual tests.

Don't modify the existing reconciliation logic, API endpoints, Angular application, or mock transaction fixtures. Avoid unnecessary new files or dependencies.

I also want to run these tests using a separate SQLite database so my existing development data and audit history remain untouched. Check how our connection string and EF Core migrations work, and provide the exact commands for setting up and running that separate database.

Run the relevant backend tests and explain every file you change. Don't commit yet.
```

Developer's explicit reply about the default scenario:

```text
Restore Baseline default
```

Manual-testing documentation request (excerpt):

```text
3. **Recommended testing sequence:**
   - Baseline: Import the initial six transactions.
   - Baseline repeated: Verify idempotency and no duplicate audit records.
   - Changes: Verify transaction updates, new insertions, and revocation of transaction 102.
   - Changes repeated: Confirm no additional changes or audits.
   - Reactivation: Verify transaction 102 becomes Active again.
   - Incomplete: Verify incomplete snapshots are skipped without unintended revocations.
   - Failure: Verify source retrieval errors are handled without losing existing data.
   - 24-hour rule: Explain why an older missing transaction, such as 104, should remain Active when absent from a subsequent complete snapshot.
4. **Expected results:** Include a concise table showing expected transaction counts, statuses, and audit actions for each scenario, based on the actual mock fixtures. Clearly state when results depend on the order of imports or starting with an empty database.
```

</details>

**Implementation and review:** I chose not to loosen the revocation rule or change the system clock. Instead, Codex added development-only settings for an explicit mock scenario and fixed UTC clock. I used an isolated SQLite database to run Baseline, Changes, and Reactivation against the same persisted state, while leaving ordinary development data untouched. I restored Baseline as the default after testing. A deliberate Uptown fixture change from `$24.99` to `$34.99` also revealed an outdated test assertion; after updating that expectation, all 38 existing tests passed. The lifecycle then produced the expected Created, Updated, Revoked, and Reactivated events.

### Phase 9: Transaction details, audit views, and documentation

I extended the frontend to make auditability useful to an evaluator without requiring direct database access. I refined the initial transaction-only concept into two complementary views: a global audit table for reviewing all changes and a transaction details dialog showing that record's own history immediately below its details. Both views needed to read the same persisted audit data.

<details>
<summary>Original prompt excerpts</summary>

Audit API requirements:

```text
### 2. Backend — Audit History APIs

Add read-only endpoints for retrieving audit records:

- `GET /api/audits` — Returns audit events across all transactions.
- `GET /api/transactions/{sourceSystem}/{transactionId}/audits` — Returns audit events for one transaction.

Requirements:
- Use `(SourceSystem, TransactionId)` to identify individual transactions, since IDs may repeat across source systems.
- Include the audit action, source system, transaction ID, field name, old value, new value, and timestamp.
- Support Created, Updated, Revoked, and Reactivated events.
- Return records newest first, with deterministic ordering when timestamps match.
- Handle nonexistent transactions appropriately.
- Query the existing SQLite audit records using EF Core without modifying them.

Do not introduce new tables, migrations, or unnecessary services.
```

Global history, transaction details, and selected history:

```text
### 3. Angular — Global Audit History

Add a **Show Audit History** button alongside the existing dashboard controls.

When clicked:
- Display an Audit History view within the existing application.
- Show a table containing audit events from all three source systems.
- Include timestamp, source system, transaction ID, action, changed field, old value, and new value.
- Display records newest first.
- Use clear visual indicators for Created, Updated, Revoked, and Reactivated events.
- Include loading, empty, and error states.
- Provide a **Back to Transactions** button that returns to the main dashboard.

Fetch this data using `GET /api/audits`. Do not hardcode audit records.

Keep the existing teal-and-white design and make the table usable on smaller screens.

### 4. Angular — Transaction Details and Individual Audit History

Make each transaction row on the main dashboard clickable.

Clicking a transaction should open a details panel or modal containing two sections:

**Section A: Transaction Details**

Display:
- Source system
- Transaction ID
- Masked card number
- Location code
- Product name
- Amount
- Transaction time
- Current status

Reuse the data from the existing transaction API wherever possible. If any required field is unavailable, inspect the backend and make the smallest appropriate read-only API adjustment without breaking the existing dashboard contract. Never expose unmasked card information.

**Section B: Audit History**

Directly below the transaction details, display the selected transaction's audit history automatically.

Use the transaction-specific audit endpoint.

Show each event's action, timestamp, field name, and old/new values where applicable.

For example:
- Created — Initial transaction import.
- Updated — Amount changed from $29.99 to $39.99.
- Revoked — Transaction status changed to Revoked.
- Reactivated — Transaction status restored to Active.

Use the actual audit data and avoid implying changes that were not recorded.

The panel should have a clear close button. Switching between transactions must load the correct audit history without displaying stale records from the previously selected transaction.
```

Focused verification request:

```text
### 6. Testing and Verification

Add focused backend tests covering:
- Global audit retrieval.
- Transaction-specific audit retrieval.
- Correct isolation between identical transaction IDs in different source systems.
- Nonexistent transactions.
- Consistent ordering.
- Correct audit event information.

Run the existing backend tests and Angular production build.

Also provide manual verification steps using our existing **Baseline → Changes → Reactivation** workflow to confirm:

1. Baseline creates audit records visible in both views.
2. Changes produces field-level Updated records and eligible Revoked records.
3. Reactivation produces Reactivated records.
4. Repeating identical imports does not create duplicate audits.
5. The global audit table includes records from all sources.
6. Selecting a transaction displays only that transaction's history.
7. Navigation, filtering, sorting, and importing continue to work.

### 7. Scope

- No changes to mock scenarios, fixed-clock testing, or reconciliation logic.
- No pagination, authentication, new UI libraries, or unrelated features.
- Avoid unnecessary files and complexity.
- Do not update README.md yet; we'll revise documentation after verifying the feature.
- Maintain consistency with the current dashboard design.
```

Final README disclosure instructions:

```text
**AI Assistance and Development Effort**
Ensure the README addresses the coding exercise's AI usage disclosure and development-time requirements.

We will provide the detailed prompt history and design discussions separately. The README can reference that documentation rather than reproducing every prompt.

Do not invent actual development hours or claim that AI assistance was not used. Identify any missing information needed to complete the required disclosure.
```

</details>

**Implementation and review:** Codex added read-only global and transaction-specific audit endpoints, masked-card safeguards, and the two Angular audit views. I verified that individual histories remain isolated by composite identity and that the new navigation does not break filtering, sorting, import, or refresh. I also tested filter retention and dialog scrolling in the browser. The expanded backend suite passed 56 tests, and the Angular production build succeeded. Codex assisted with the README; I reviewed its technical accuracy and kept the setup and manual-testing instructions practical.

### Phase 10: Final dependency and monetary precision fixes

A final review identified Vite development-tooling advisories and precision loss when JavaScript parsed large valid transaction amounts as numbers. I first requested investigation without implementation, then approved a compatible builder patch and exact decimal strings at the API boundary. I required the existing decimal model, integer-cent storage, reconciliation logic, and my manually edited README to remain unchanged.

<details>
<summary>Exact excerpts from the final implementation request</summary>

```text
### 2. Angular dependency security update

Proceed with the previously recommended patch update from `@angular/build` 22.2.4 to 22.2.5, provided it remains compatible.

- Update only the required development dependencies and lockfile.
- Verify that the resolved Vite version includes the relevant security fixes.
- Run `npm ci`, `npm audit`, and the Angular production build.
- Verify that the Angular development server and API proxy still function correctly.
- Review the dependency diff to ensure no unnecessary packages were upgraded.

Avoid unrelated framework updates or application changes.

### 3. Fix monetary precision

Proceed with the recommended approach of representing transaction amounts as exact decimal strings in the API response.

- Preserve the existing C# decimal model, EF Core mappings, SQLite integer-cent storage, and reconciliation logic.
- Serialize transaction amounts as invariant decimal strings with exactly two decimal places.
- Update the Angular transaction model to use strings for amounts.
- Format amounts without converting them to JavaScript numbers.
- Use `bigint` cents for exact amount comparisons when sorting.
- Preserve the existing ascending and descending sorting behavior.
- Ensure the transaction table and details view display amounts consistently.
- Do not introduce additional libraries unless absolutely necessary.

Add focused tests for normal amounts, zero, negative amounts, maximum supported values, and large values differing by one cent.

Pay attention to the API response contract change from a JSON number to a string. Check for any other code that consumes the amount field and update it where necessary.
```

</details>

**Implementation and review:** Codex updated `@angular/build` to 22.2.5, resolving Vite 8.3.5 and its patched dependencies without upgrading the Angular runtime or CLI. Codex changed the transaction response's `amount` from a JSON number to an invariant two-decimal string, updated Angular formatting and sorting to preserve that representation, and added focused SQLite/API tests plus frontend money tests using Node's built-in runner. This was a coordinated API contract change; other consumers expecting a numeric amount would need updating. No entity, database mapping, migration, or reconciliation change was needed.

**Verification:** Codex recorded 67 passing backend tests, 13 passing frontend money tests, successful solution and Angular production builds, and zero advisories from `npm audit` after `npm ci`. Runtime checks through the Angular proxy used an isolated SQLite database and the fixed UTC clock for Baseline, Changes, Reactivation, repeated imports, Incomplete, and Failure. Browser checks confirmed filtering, refresh, details, audit navigation, and exact display and sorting of maximum values and amounts one cent apart. Existing development database files were verified unchanged. I then requested focused documentation corrections while preserving my README AI Assistance wording and all earlier prompt quotations.

## Design decisions and clarifications

Several decisions became clearer during review, and I used those discussions to narrow the implementation rather than add features without a clear purpose.

- **Source snapshots versus stored state.** I kept the mock sources responsible only for returning transaction snapshots. The reconciliation service owns comparisons, status changes, and audit creation. That separation made the scenarios repeatable and the business behavior easier to test.
- **Revocation semantics.** I chose the interval `[reference time - 24 hours, reference time)` for a missing Active transaction, conditional on a complete, successful snapshot. An older missing transaction remains Active, and a delayed incoming transaction can still be imported.
- **Testing through the actual application.** I chose to trigger imports from Angular while selecting mock scenarios through development configuration. This exercised the full workflow without building a special scenario-management screen or endpoint.
- **Two levels of audit visibility.** I refined the UI to include a global audit table and a details dialog with transaction-specific history. The two views answer different questions while using the same audit data.
- **Deliberate scope control.** I kept the architecture compact and deferred optional finalization and pagination. The goal was to thoroughly validate the required behavior before considering additional complexity.

## Modifications and corrections to AI-generated output

I reviewed Codex's implementation at each checkpoint and directed the following substantive revisions. Codex performed many of the code changes; I also made and tested local fixture changes during manual verification.

- **Database contract and configuration:** I clarified the composite identity, exact money representation, and UTC handling. I also identified and requested fixes for SQLite path resolution and invalid development ports before the initial migration was applied.
- **Mock data:** I specified realistic electronics transactions, source-specific masked payment cards, and empty card fields for non-card payments. I corrected the initial assumption that matching transaction IDs across stores should imply matching card values.
- **Reconciliation verification:** I tested the 24-hour logic with stored records rather than relying only on expected counts. The resulting fixed-clock testing configuration was added for Development only, leaving the reconciliation rule unchanged.
- **Frontend experience:** I directed the dashboard redesign, application branding, global audit view, and transaction details dialog. After testing navigation, a source-filter selection issue was corrected so the chosen filter remained visible after returning from Audit History.
- **Documentation and validation:** I reviewed generated code diffs, verified browser behavior and SQLite results, and refined the README to prioritize reproducible setup and scenario testing over unnecessary implementation detail.

## Testing and review

I tested the running Angular application against the ASP.NET Core API and inspected transactions and audit records in SQLite. I exercised the Baseline, Changes, and Reactivation lifecycle, checked repeat imports, and reviewed the incomplete and failure behaviors. I also inspected the changes in the backend controllers, import configuration, tests, and Angular templates before committing them.

The final recorded verification passed **67 backend tests and 13 frontend money tests**, the solution build, and the Angular production build. I manually verified the global and transaction-specific audit views, filter preservation, navigation, and dialog scrolling. The backend controller tests run against SQLite but invoke controller actions directly; frontend money tests use Node's built-in runner. I did not add an automated browser end-to-end suite.

## Notes on the prompt record

The quoted blocks preserve the available original excerpts, including their wording and formatting. Some longer prompts and earlier discussion messages were not recoverable in full, so this document does not claim to be a complete verbatim transcript. I have not reconstructed missing prompt text. Where full prompts are required, the original Codex and ChatGPT conversation records should be included as well.

AI assistance was used in drafting some prompts, generating and revising much of the implementation, checking code, and preparing documentation. The descriptions above identify the development decisions I made, the changes produced with AI assistance, and the testing I performed. They do not represent AI-generated code as independently handwritten.
