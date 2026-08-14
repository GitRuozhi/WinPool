using WinPool.Application;
using WinPool.Testing.Tools;
using WinPool.ToolManagement;

namespace WinPool.Agent;

/// <summary>
/// A validated test step paired with the exact external-tool invocation, when
/// the step is executed out of process.
/// </summary>
internal sealed record PreparedExecutionStep(
    TestStep Step,
    ToolProcessRequest? Request,
    IExternalToolAdapter? Adapter);
