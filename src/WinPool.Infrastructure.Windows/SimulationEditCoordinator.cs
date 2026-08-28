using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WinPool.Application;
using WinPool.Domain;
using WinPool.Execution;
using ExecutionMode = WinPool.Domain.ExecutionMode;
using PrivilegeState = WinPool.Domain.PrivilegeState;

namespace WinPool.Infrastructure.Windows;

public sealed record SimulationEditCommit(
    StorageSystemDocument Document,
    OperationPlan Plan,
    IReadOnlyList<ExecutionEvent> Events);

/// <summary>
/// Transitional adapter that routes the accepted V0.13 simulation document model
/// through the V0.2 planner, policy, one-shot authorization and simulation executor.
/// It never exposes a local-storage mutation executor.
/// </summary>
public sealed class SimulationEditCoordinator(
    Func<StorageSystemDocument> currentDocument,
    Func<SimulationEditCommit, CancellationToken, Task> commitDocument,
    ISimulationOperationService simulationEditor,
    Func<StorageSystemDocument, SimulationOperationResult>? resetEditor = null) : ISimulationEditCoordinator
{
    private readonly Func<StorageSystemDocument> currentDocument =
        currentDocument ?? throw new ArgumentNullException(nameof(currentDocument));
    private readonly Func<SimulationEditCommit, CancellationToken, Task> commitDocument =
        commitDocument ?? throw new ArgumentNullException(nameof(commitDocument));
    private readonly ISimulationOperationService simulationEditor =
        simulationEditor ?? throw new ArgumentNullException(nameof(simulationEditor));
    private readonly Func<StorageSystemDocument, SimulationOperationResult>? resetEditor = resetEditor;

    public async Task<ApplicationResult<SimulationEditReceipt>> ExecuteAsync(
        SimulationEditRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var correlationId = CorrelationId.New();
        var document = currentDocument();
        if (document.Kind != StorageSystemKind.Simulation)
        {
            return Failure(
                ApplicationStatus.Rejected,
                correlationId,
                "simulation.local-read-only",
                "Local storage systems are read-only.");
        }

        if (request.Kind == SimulationEditKind.ResetDocument
            && !document.Id.StartsWith("simulation:builtin", StringComparison.Ordinal))
        {
            return Failure(
                ApplicationStatus.Rejected,
                correlationId,
                "simulation.reset-built-in-only",
                "Only the built-in simulation document can be reset.");
        }

        var targetUnit = ResolveTarget(document.Snapshot, request);
        if (targetUnit is null)
        {
            return Failure(
                ApplicationStatus.Rejected,
                correlationId,
                "simulation.target-missing",
                "The selected simulation target no longer exists.");
        }

        var systemId = document.SystemId;
        var target = new StorageObjectId(
            systemId,
            MapKind(targetUnit.Kind),
            targetUnit.StableId);
        var targets = new List<StorageObjectId> { target };
        foreach (var memberKey in request.MemberDiskIds ?? [])
        {
            var member = document.Snapshot.FindUnit(memberKey);
            if (member is null || member.Kind != StorageUnitKind.PhysicalDisk)
            {
                return Failure(
                    ApplicationStatus.Rejected,
                    correlationId,
                    "simulation.member-target-missing",
                    "A selected member disk no longer exists.");
            }

            targets.Add(new StorageObjectId(systemId, StorageObjectKind.PhysicalDisk, member.StableId));
        }

        var inventoryVersion = InventoryVersion(document);
        var environmentId = InternalStableIdentity.EnvironmentFromDocumentId(document.Id);
        var machineBinding = MachineBinding.Create(["winpool-simulation", document.Id]);
        var environment = new EnvironmentProfile(
            environmentId,
            EnvironmentKind.Simulation,
            machineBinding,
            ExecutionCapability.SimulateStorageMutation,
            IsUserProvidedDisposableEnvironment: false,
            DateTimeOffset.UtcNow);
        var context = new WinPool.Execution.ExecutionContext(
            environment,
            ExecutionMode.Simulation,
            PrivilegeState.StandardUser,
            machineBinding,
            inventoryVersion,
            IsReleaseBuild: true);
        var parameters = Parameters(request);
        var operationRequest = new OperationRequest(
            OperationId.New(),
            environmentId,
            systemId,
            OperationIntent.SimulateStorageMutation,
            targets,
            parameters,
            DateTimeOffset.UtcNow);

        try
        {
            var policy = new OperationPolicyEvaluator();
            var authority = new InMemoryOperationAuthority(policy);
            var planner = new DefaultOperationPlanner(
                new FixedOperationInventoryVersionSource(inventoryVersion));
            var plan = await planner.BuildAsync(operationRequest, cancellationToken);
            var authorization = await authority.AuthorizeAsync(
                    plan,
                    context,
                    userConfirmed: false,
                    cancellationToken);
            if (authorization.Kind != AuthorizationIssueKind.Issued || authorization.Token is null)
            {
                return Failure(
                    authorization.Kind == AuthorizationIssueKind.Rejected
                        ? ApplicationStatus.Rejected
                        : ApplicationStatus.RequiresAuthorization,
                    correlationId,
                    authorization.Code,
                    authorization.Message);
            }

            var store = new ApplicationSimulationDocumentStore(
                document,
                request,
                parameters,
                simulationEditor,
                resetEditor);
            var executor = new SimulationOperationExecutor(store);
            var gate = new ExecutorGate(policy, authority);
            ExecutionEvent? terminal = null;
            var executionEvents = new List<ExecutionEvent>();
            await foreach (var executionEvent in gate.ExecuteAsync(
                               plan,
                               context,
                               authorization.Token,
                               executor,
                               cancellationToken))
            {
                executionEvents.Add(executionEvent);
                if (executionEvent.Kind is ExecutionEventKind.Completed
                    or ExecutionEventKind.Cancelled
                    or ExecutionEventKind.Rejected
                    or ExecutionEventKind.Failed)
                {
                    terminal = executionEvent;
                }
            }

            if (terminal?.Kind != ExecutionEventKind.Completed || store.Result is null)
            {
                var status = terminal?.Kind == ExecutionEventKind.Cancelled
                    ? ApplicationStatus.Cancelled
                    : terminal?.Kind == ExecutionEventKind.Rejected
                        ? ApplicationStatus.Rejected
                        : ApplicationStatus.Failed;
                return Failure(
                    status,
                    correlationId,
                    terminal?.Code ?? "simulation.execution-incomplete",
                    store.FailureText ?? terminal?.Message ?? "The simulation operation did not complete.");
            }

            await commitDocument(
                new SimulationEditCommit(
                    store.Result.Document,
                    plan,
                    executionEvents),
                cancellationToken);
            return ApplicationResult<SimulationEditReceipt>.Succeeded(
                new SimulationEditReceipt(
                    plan.OperationId,
                    plan.PlanHash,
                    systemId,
                    target,
                    store.BeforeRevision,
                    store.AfterRevision,
                    store.Result.Commands),
                correlationId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Failure(
                ApplicationStatus.Cancelled,
                correlationId,
                "simulation.cancelled",
                "The simulation operation was cancelled.");
        }
        catch (Exception exception) when (exception is
            ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            return Failure(
                ApplicationStatus.Failed,
                correlationId,
                "simulation.execution-failed",
                "The simulation operation failed without changing the active document.");
        }
    }

    private static ApplicationResult<SimulationEditReceipt> Failure(
        ApplicationStatus status,
        CorrelationId correlationId,
        string code,
        string userText) =>
        ApplicationResult<SimulationEditReceipt>.FromStatus(
            status,
            correlationId,
            new ApplicationMessage(
                code,
                userText,
                string.Empty,
                status == ApplicationStatus.Rejected
                    ? ApplicationMessageSeverity.Warning
                    : ApplicationMessageSeverity.Error,
                []));

    private static IReadOnlyDictionary<string, string> Parameters(SimulationEditRequest request)
    {
        var values = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["EditKind"] = request.Kind.ToString(),
            ["TargetProviderKey"] = request.TargetProviderKey
        };
        Add(values, "Name", request.Name);
        Add(values, "DriveLetter", request.DriveLetter);
        Add(values, "FileSystem", request.FileSystem);
        Add(values, "AllocationUnitSize", request.AllocationUnitSize);
        Add(values, "Offline", request.Offline);
        Add(values, "EstimatedWriteBytes", request.SizeBytes);
        Add(values, "CreateMsr", request.CreateMsr);
        Add(values, "InterleaveBytes", request.InterleaveBytes);
        Add(values, "Resiliency", request.Resiliency);
        if (request.MemberDiskIds is not null)
        {
            values["MemberDiskProviderKeys"] = JsonSerializer.Serialize(
                request.MemberDiskIds.Order(StringComparer.Ordinal));
        }

        return values;
    }

    private static void Add(
        IDictionary<string, string> values,
        string key,
        string? value)
    {
        if (value is not null)
        {
            values[key] = value;
        }
    }

    private static void Add<T>(
        IDictionary<string, string> values,
        string key,
        T? value) where T : struct
    {
        if (value is not null)
        {
            values[key] = Convert.ToString(value.Value, CultureInfo.InvariantCulture)!;
        }
    }

    private static string InventoryVersion(StorageSystemDocument document) =>
        $"application:{document.SchemaVersion}:{document.Snapshot.SchemaVersion}:{document.UpdatedAt.UtcTicks}:{document.Snapshot.ScannedAt.UtcTicks}";

    private static StorageUnitRef? ResolveTarget(
        StorageSnapshot snapshot,
        SimulationEditRequest request)
    {
        var direct = snapshot.FindUnit(request.TargetProviderKey);
        if (direct is not null)
        {
            return direct;
        }

        if (request.Kind == SimulationEditKind.CreateStoragePool
            && StringComparer.OrdinalIgnoreCase.Equals(request.TargetProviderKey, "primordial"))
        {
            var primordial = snapshot.StoragePools.SingleOrDefault(pool => pool.IsPrimordial);
            return primordial is null ? null : snapshot.FindUnit(primordial.StableId);
        }

        return null;
    }

    private static StorageObjectKind MapKind(StorageUnitKind kind) => kind switch
    {
        StorageUnitKind.System => StorageObjectKind.System,
        StorageUnitKind.StorageSubsystem => StorageObjectKind.StorageSubsystem,
        StorageUnitKind.StoragePool => StorageObjectKind.StoragePool,
        StorageUnitKind.StorageTier => StorageObjectKind.StorageTier,
        StorageUnitKind.PhysicalDisk => StorageObjectKind.PhysicalDisk,
        StorageUnitKind.VirtualDisk => StorageObjectKind.VirtualDisk,
        StorageUnitKind.OsDisk => StorageObjectKind.OsDisk,
        StorageUnitKind.Partition => StorageObjectKind.Partition,
        StorageUnitKind.NetworkDisk => StorageObjectKind.NetworkDisk,
        StorageUnitKind.NetworkDiskGroup or StorageUnitKind.OtherDiskGroup
            or StorageUnitKind.DirectDiskGroup or StorageUnitKind.VirtualDiskGroup =>
            StorageObjectKind.LogicalGroup,
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private sealed class ApplicationSimulationDocumentStore(
        StorageSystemDocument document,
        SimulationEditRequest request,
        IReadOnlyDictionary<string, string> expectedParameters,
        ISimulationOperationService editor,
        Func<StorageSystemDocument, SimulationOperationResult>? resetEditor) : ISimulationDocumentStore
    {
        private StorageSystemDocument current = document;
        private StorageSystemDocument? beforeDocument;

        public long BeforeRevision { get; private set; }
        public long AfterRevision { get; private set; }
        public SimulationOperationResult? Result { get; private set; }
        public string? FailureText { get; private set; }

        public Task<SimulationMutationReceipt> ApplyAsync(
            OperationPlan plan,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!DictionaryEqual(plan.Parameters, expectedParameters))
            {
                throw new InvalidOperationException("The simulation edit parameters changed after planning.");
            }

            beforeDocument = current;
            BeforeRevision = current.Revision;
            var result = request.Kind == SimulationEditKind.ResetDocument
                ? resetEditor?.Invoke(current)
                    ?? SimulationOperationResult.Failure(
                        current,
                        "The built-in simulation reset adapter is unavailable.")
                : editor.Apply(current, ToApplicationRequest(request));
            if (!result.Succeeded)
            {
                FailureText = result.Error;
                throw new InvalidOperationException("The application simulation document adapter rejected the edit.");
            }

            AfterRevision = checked(BeforeRevision + 1);
            Result = result with
            {
                Document = result.Document with { Revision = AfterRevision }
            };
            current = Result.Document;
            return Task.FromResult(
                new SimulationMutationReceipt(
                    SimulationDocumentSnapshot.Create(plan.SystemId, BeforeRevision, expectedParameters),
                    SimulationDocumentSnapshot.Create(plan.SystemId, AfterRevision, expectedParameters)));
        }

        public Task RestoreAsync(
            SimulationMutationReceipt receipt,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (beforeDocument is null || receipt.After.Revision != AfterRevision)
            {
                throw new InvalidOperationException("The simulation revision cannot be restored.");
            }

            current = beforeDocument;
            Result = null;
            return Task.CompletedTask;
        }

        private static bool DictionaryEqual(
            IReadOnlyDictionary<string, string> left,
            IReadOnlyDictionary<string, string> right) =>
            left.Count == right.Count
            && left.All(pair => right.TryGetValue(pair.Key, out var value)
                && StringComparer.Ordinal.Equals(pair.Value, value));

        private static SimulationOperationRequest ToApplicationRequest(SimulationEditRequest request) =>
            new(
                Enum.Parse<SimulationOperationKind>(request.Kind.ToString()),
                request.TargetProviderKey,
                request.Name,
                request.DriveLetter,
                request.FileSystem,
                request.AllocationUnitSize,
                request.Offline,
                request.SizeBytes,
                request.CreateMsr,
                request.InterleaveBytes,
                request.Resiliency,
                request.MemberDiskIds);
    }
}
