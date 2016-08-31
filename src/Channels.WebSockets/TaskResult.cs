using System.Threading.Tasks;

namespace Channels.WebSockets
{
    internal static class TaskResult
    {
        public static readonly Task<bool> True = Task.FromResult(true), False = Task.FromResult(false);
        public static readonly Task<int> Zero = Task.FromResult(0);
    }
}
