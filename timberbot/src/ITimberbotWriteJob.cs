// ITimberbotWriteJob.cs. Budgeted write execution on the main thread.
//
// WHY THIS EXISTS
// ---------------
// Some write operations (like placing a long A* path) can take many milliseconds.
// Running them all in one frame stalls the game. Write jobs spread the work across
// multiple frames with a configurable per-frame budget (default 1ms).
//
// HOW IT WORKS
// ------------
// 1. HttpServer creates a write job (e.g. LambdaWriteJob wrapping a placement call)
// 2. The HTTP response waits (the request stays open)
// 3. Each frame, ProcessWriteJobs() calls Step() on pending jobs until the budget runs out
// 4. When IsCompleted = true, the HTTP response is sent with the Result
//
// LambdaWriteJob also supports "settle frames". waiting N frames after the action
// completes before reporting done, so the game has time to process side effects.

namespace Timberbot
{
    internal interface ITimberbotWriteJob
    {
        string Name { get; }
        bool IsCompleted { get; }
        int StatusCode { get; }
        object Result { get; }
        void Step(float now, double budgetMs);
        void Cancel(string error);
    }

    internal sealed class LambdaWriteJob : ITimberbotWriteJob
    {
        private readonly string _name;
        private readonly System.Func<object> _action;
        private readonly int _settleFrames;
        private bool _started;
        private int _settleFramesRemaining;
        private bool _completed;
        private int _statusCode = 200;
        private object _result;

        public LambdaWriteJob(string name, System.Func<object> action, int settleFrames = 1)
        {
            _name = name;
            _action = action;
            _settleFrames = settleFrames;
        }

        public string Name => _name;
        public bool IsCompleted => _completed;
        public int StatusCode => _statusCode;
        public object Result => _result;

        public void Step(float now, double budgetMs)
        {
            if (_completed) return;
            if (!_started)
            {
                _result = _action();
                _started = true;
                _settleFramesRemaining = _settleFrames;
                if (_settleFramesRemaining <= 0)
                    _completed = true;
                return;
            }

            if (_settleFramesRemaining > 0)
            {
                _settleFramesRemaining--;
                if (_settleFramesRemaining == 0)
                    _completed = true;
            }
        }

        public void Cancel(string error)
        {
            if (_completed) return;
            _statusCode = 500;
            _result = "{\"error\":\"" + error.Replace("\"", "'") + "\"}";
            _completed = true;
        }
    }
}
