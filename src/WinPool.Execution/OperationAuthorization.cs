using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using WinPool.Domain;

namespace WinPool.Execution;

public sealed record OperationAuthorizationToken(
    string TokenId,
    string PlanHash,
    EnvironmentId EnvironmentId,
    string MachineBinding,
    string InventoryVersion,
    string TargetFingerprint,
    DateTimeOffset IssuedAt,
    DateTimeOffset ExpiresAt);

public enum AuthorizationIssueKind
{
    Issued,
    ConfirmationRequired,
    Rejected
}

public sealed record AuthorizationIssueResult(
    AuthorizationIssueKind Kind,
    OperationAuthorizationToken? Token,
    string Code,
    string Message);

public enum AuthorizationValidationKind
{
    Valid,
    UnknownToken,
    Expired,
    AlreadyUsed,
    PlanHashMismatch,
    InventoryMismatch,
    MachineMismatch,
    EnvironmentMismatch,
    TargetMismatch
}

public sealed record AuthorizationValidationResult(
    AuthorizationValidationKind Kind,
    string Code,
    string Message)
{
    public bool IsValid => Kind == AuthorizationValidationKind.Valid;
}

public interface IOperationAuthority
{
    Task<AuthorizationIssueResult> AuthorizeAsync(
        OperationPlan plan,
        ExecutionContext context,
        bool userConfirmed,
        CancellationToken cancellationToken);

    AuthorizationValidationResult Consume(
        OperationAuthorizationToken token,
        OperationPlan plan,
        ExecutionContext context);
}

public sealed class InMemoryOperationAuthority : IOperationAuthority
{
    public static readonly TimeSpan DefaultLifetime = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan MaximumLifetime = TimeSpan.FromMinutes(5);

    private readonly IOperationPolicyEvaluator _policy;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _lifetime;
    private readonly ConcurrentDictionary<string, GrantState> _grants = new(StringComparer.Ordinal);

    public InMemoryOperationAuthority(
        IOperationPolicyEvaluator policy,
        TimeProvider? timeProvider = null,
        TimeSpan? lifetime = null)
    {
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _lifetime = lifetime ?? DefaultLifetime;

        if (_lifetime <= TimeSpan.Zero || _lifetime > MaximumLifetime)
        {
            throw new ArgumentOutOfRangeException(nameof(lifetime), $"Authorization lifetime must be greater than zero and no longer than {MaximumLifetime}.");
        }
    }

    public async Task<AuthorizationIssueResult> AuthorizeAsync(
        OperationPlan plan,
        ExecutionContext context,
        bool userConfirmed,
        CancellationToken cancellationToken)
    {
        var decision = await _policy.EvaluateAsync(plan, context, cancellationToken).ConfigureAwait(false);
        if (decision.Kind == PolicyDecisionKind.Rejected)
        {
            return new(AuthorizationIssueKind.Rejected, null, decision.Code, decision.Message);
        }

        if (decision.Kind == PolicyDecisionKind.RequiresConfirmation && !userConfirmed)
        {
            return new(AuthorizationIssueKind.ConfirmationRequired, null, decision.Code, decision.Message);
        }

        var now = _timeProvider.GetUtcNow();
        var token = new OperationAuthorizationToken(
            CreateTokenId(),
            plan.PlanHash,
            plan.EnvironmentId,
            context.CurrentMachineBinding,
            plan.InventoryVersion,
            ComputeTargetFingerprint(plan.Targets),
            now,
            now.Add(_lifetime));

        if (!_grants.TryAdd(token.TokenId, new GrantState(token, false)))
        {
            throw new InvalidOperationException("A cryptographically random authorization token id collided.");
        }

        return new(AuthorizationIssueKind.Issued, token, "authority.issued", "A short-lived one-time authorization was issued.");
    }

    public AuthorizationValidationResult Consume(
        OperationAuthorizationToken token,
        OperationPlan plan,
        ExecutionContext context)
    {
        ArgumentNullException.ThrowIfNull(token);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(context);

        if (!_grants.TryGetValue(token.TokenId, out var state) || state.Token != token)
        {
            return Invalid(AuthorizationValidationKind.UnknownToken, "authority.unknown-token", "The authorization token is unknown or was altered.");
        }

        if (state.IsConsumed)
        {
            return Invalid(AuthorizationValidationKind.AlreadyUsed, "authority.already-used", "The authorization token has already been used.");
        }

        if (_timeProvider.GetUtcNow() >= token.ExpiresAt)
        {
            return Invalid(AuthorizationValidationKind.Expired, "authority.expired", "The authorization token expired.");
        }

        if (token.EnvironmentId != plan.EnvironmentId ||
            token.EnvironmentId != context.Environment.Id)
        {
            return Invalid(AuthorizationValidationKind.EnvironmentMismatch, "authority.environment-mismatch", "The authorization belongs to another environment.");
        }

        if (!StringComparer.Ordinal.Equals(token.MachineBinding, context.CurrentMachineBinding) ||
            !StringComparer.Ordinal.Equals(token.MachineBinding, context.Environment.MachineBinding))
        {
            return Invalid(AuthorizationValidationKind.MachineMismatch, "authority.machine-mismatch", "The authorization belongs to another machine.");
        }

        if (!StringComparer.Ordinal.Equals(token.InventoryVersion, plan.InventoryVersion) ||
            !StringComparer.Ordinal.Equals(token.InventoryVersion, context.CurrentInventoryVersion))
        {
            return Invalid(AuthorizationValidationKind.InventoryMismatch, "authority.inventory-mismatch", "The inventory changed after authorization.");
        }

        if (!StringComparer.Ordinal.Equals(token.TargetFingerprint, ComputeTargetFingerprint(plan.Targets)))
        {
            return Invalid(AuthorizationValidationKind.TargetMismatch, "authority.target-mismatch", "The target set changed after authorization.");
        }

        var computedPlanHash = OperationPlanHasher.Compute(plan);
        if (!StringComparer.Ordinal.Equals(plan.PlanHash, computedPlanHash) ||
            !StringComparer.Ordinal.Equals(token.PlanHash, plan.PlanHash))
        {
            return Invalid(AuthorizationValidationKind.PlanHashMismatch, "authority.plan-hash-mismatch", "The authorization does not match this plan.");
        }

        var consumed = state with { IsConsumed = true };
        if (!_grants.TryUpdate(token.TokenId, consumed, state))
        {
            return Invalid(AuthorizationValidationKind.AlreadyUsed, "authority.already-used", "The authorization token was consumed concurrently.");
        }

        return new(AuthorizationValidationKind.Valid, "authority.valid", "The authorization is valid and has been consumed.");
    }

    internal static string ComputeTargetFingerprint(IEnumerable<StorageObjectId> targets)
    {
        var canonical = string.Join(
            "\n",
            targets
                .Select(target => $"{target.System.Value:D}|{(int)target.Kind}|{target.ProviderKey}")
                .Order(StringComparer.Ordinal));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private static string CreateTokenId() =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

    private static AuthorizationValidationResult Invalid(
        AuthorizationValidationKind kind,
        string code,
        string message) =>
        new(kind, code, message);

    private sealed record GrantState(OperationAuthorizationToken Token, bool IsConsumed);
}
