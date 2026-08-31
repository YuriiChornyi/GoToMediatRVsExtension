using System.Threading.Tasks;
using Microsoft.CodeAnalysis;

namespace VSIXExtension.Services
{
    /// <summary>
    /// Test-only stand-in for the real <c>VSIXExtension.Services.WorkspaceService</c>.
    ///
    /// The real one wraps <c>VisualStudioWorkspace</c> and drags in Microsoft.VisualStudio.*, so it
    /// cannot be linked into this project. <see cref="MediatRUsageFinder"/> only ever calls
    /// <see cref="GetWorkspaceAsync"/> and reads <c>CurrentSolution</c> off the result, so this
    /// narrow stub is enough to exercise the finder against an in-memory solution.
    ///
    /// If MediatRUsageFinder starts using more of WorkspaceService, this file stops compiling -
    /// which is the intended signal to widen the stub (or extract a Solution-based seam in the
    /// production code).
    /// </summary>
    public class WorkspaceService
    {
        private readonly Workspace _workspace;

        public WorkspaceService(Workspace workspace)
        {
            _workspace = workspace;
        }

        public Task<Workspace> GetWorkspaceAsync() => Task.FromResult(_workspace);
    }
}
