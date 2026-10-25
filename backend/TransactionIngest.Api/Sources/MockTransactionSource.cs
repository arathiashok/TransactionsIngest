namespace TransactionIngest.Api.Sources;

public enum MockScenario
{
    Baseline,
    Changes,
    Reactivation,
    Incomplete,
    Failure
}

public sealed class MockScenarioState
{
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private volatile MockScenario _scenario = MockScenario.Baseline;

    public MockScenario Scenario
    {
        get => _scenario;
        set
        {
            if (!Enum.IsDefined(value))
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown mock scenario.");
            }

            if (!_operationGate.Wait(0))
            {
                throw new InvalidOperationException("Cannot change the scenario while an import is running.");
            }

            try
            {
                _scenario = value;
            }
            finally
            {
                _operationGate.Release();
            }
        }
    }

    internal Task<bool> TryBeginImportAsync(CancellationToken cancellationToken) =>
        _operationGate.WaitAsync(0, cancellationToken);

    internal void EndImport() => _operationGate.Release();
}

public sealed class MockTransactionSource : ITransactionSource
{
    private readonly MockScenarioState _scenarioState;
    private readonly string _locationCode;

    // Fixed fixture clock; later reconciliation demos must use a matching reference time.
    public static DateTime ReferenceTimeUtc { get; } = new(2026, 10, 25, 12, 0, 0, DateTimeKind.Utc);

    public string SourceSystem { get; }

    public MockTransactionSource(string sourceSystem, MockScenarioState scenarioState)
    {
        ArgumentNullException.ThrowIfNull(scenarioState);

        _locationCode = sourceSystem switch
        {
            "Store Uptown" => "UPTOWN",
            "Store Downtown" => "DOWNTOWN",
            "Online" => "ONLINE",
            _ => throw new ArgumentException("Unknown mock source system.", nameof(sourceSystem))
        };
        SourceSystem = sourceSystem;
        _scenarioState = scenarioState;
    }

    public Task<SourceSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Read once so a single fetch cannot combine scenarios during a state change.
        var scenario = _scenarioState.Scenario;
        if (scenario == MockScenario.Failure)
        {
            return Task.FromException<SourceSnapshot>(
                new InvalidOperationException($"Mock retrieval failed for {SourceSystem}."));
        }

        // Card payments use masked last-four digits; non-card payments use an empty value.
        // Cards are explicit per-source fixtures, not derived from transaction IDs.
        var (firstCardNumber, addedCardNumber) = SourceSystem switch
        {
            "Store Uptown" => ("***1111", "***2222"),
            "Store Downtown" => ("***0002", "***3333"),
            "Online" => ("***4444", "***5555"),
            _ => throw new InvalidOperationException("Unknown mock source system.")
        };

        var (firstProduct, firstAmount, updatedAmount, secondProduct, secondAmount,
            addedProduct, addedAmount, delayedProduct, delayedAmount) = SourceSystem switch
        {
            "Store Uptown" => ("Wireless Mouse", 29.99m, 24.99m,
                "Mechanical Keyboard", 89.99m, "USB-C Adapter", 19.99m, "Mouse Pad", 12.99m),
            "Store Downtown" => ("USB-C Docking Station", 129.99m, 119.99m,
                "Over-Ear Headphones", 79.99m, "27-inch Monitor", 229.99m, "HDMI Cable", 14.99m),
            "Online" => ("Gaming Headset", 69.99m, 59.99m,
                "External SSD", 109.99m, "Webcam", 49.99m, "Laptop Stand", 34.99m),
            _ => throw new InvalidOperationException("Unknown mock source system.")
        };

        var first = new SourceTransaction(101, firstCardNumber, _locationCode, firstProduct,
            firstAmount, ReferenceTimeUtc.AddHours(-2));
        var second = new SourceTransaction(102, "", _locationCode, secondProduct,
            secondAmount, ReferenceTimeUtc.AddHours(-1));
        var updated = first with { Amount = updatedAmount };
        var added = new SourceTransaction(103, addedCardNumber, _locationCode, addedProduct,
            addedAmount, ReferenceTimeUtc.AddMinutes(-30));
        var delayed = new SourceTransaction(104, "", _locationCode, delayedProduct,
            delayedAmount, ReferenceTimeUtc.AddHours(-30));

        SourceTransaction[] transactions = scenario switch
        {
            MockScenario.Baseline => [first, second],
            MockScenario.Changes => [updated, added, delayed],
            MockScenario.Reactivation => [updated, second, added, delayed],
            MockScenario.Incomplete => [first],
            _ => throw new InvalidOperationException("Unknown mock scenario.")
        };

        return Task.FromResult(new SourceSnapshot(
            Array.AsReadOnly(transactions), scenario != MockScenario.Incomplete));
    }
}
