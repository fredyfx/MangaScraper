﻿using System;
using System.Threading;
using System.Collections.Concurrent;
using log4net;

namespace Blacker.MangaScraper.Helpers
{
    class AsyncRequestQueue
    {
        private static readonly ILog _log = LogManager.GetLogger(typeof(AsyncRequestQueue));

        private readonly AutoResetEvent _canProcess = new AutoResetEvent(false);
        private readonly AutoResetEvent _stopEvent = new AutoResetEvent(false);
        private readonly ConcurrentQueue<AsyncRequest> _requestQueue = new ConcurrentQueue<AsyncRequest>();
        private readonly SynchronizationContext _synchronizationContext;
        private readonly Thread _thread;

        private bool _initialized;

        public event EventHandler TasksCompleted;

        public AsyncRequestQueue() 
            : this(SynchronizationContext.Current)
        {
        }

        public AsyncRequestQueue(SynchronizationContext synchronizationContext)
	    {
            _synchronizationContext = synchronizationContext;
            _thread = new Thread(ThreadProc)
                          {
                              IsBackground = true
                          };

            _initialized = false;
	    }

        public void Initialize()
        {
            if(_initialized)
                return;

            try 
	        {
		        _thread.Start();
	        }
	        catch (Exception ex)
	        {
                _log.Error("Unable to start async queue thread.", ex);
                throw;
	        }

            _initialized = true;
        }

        public void Stop()
        {
            _stopEvent.Set();
            _thread.Join();
            _initialized = false;
        }

        #region Thread

        private void ThreadProc()
        {
            while (true)
	        {
                if(AutoResetEvent.WaitAny(new [] {_stopEvent, _canProcess}) == 0)
                {
                    _log.Debug("Recieved stop signal. Exiting thread.");
                    return;
                }
                else
                {
                    AsyncRequest request;

                    while (_requestQueue.TryDequeue(out request))
	                {
                        ProcessAsyncRequest(request);
	                }

                    if (_requestQueue.Count == 0)
                    {
                        OnTasksCompleted();
                    }
                }
            }
        }

        private void ProcessAsyncRequest(AsyncRequest request)
        {
            request.Error = null;

            try
            {
                request.Result = request.Method();
            }
            catch (Exception ex)
            {
                _log.Error("Call to requested method failed with exception.", ex);
                request.Result = null;
                request.Error = ex;
                // do nothing here we will send error info when invoking callback
            }

            try
            {
                if (_synchronizationContext == SynchronizationContext.Current)
                {
                    // Execute callback on the current thread
                    request.Callback(request.Result, request.Error);
                }
                else
                {
                    // Post the callback on the creator thread
                    _synchronizationContext.Post(new SendOrPostCallback(delegate(object state)
                    {
                        var r = state as AsyncRequest;
                        r.Callback(r.Result, r.Error);
                    }), request);
                }
            }
            catch (Exception ex)
            {
                _log.Error("Unable to invoke callback method.", ex);
            }
        }

        private void OnTasksCompleted()
        {
            if (_synchronizationContext == SynchronizationContext.Current)
            {
                var tasksCompleted = TasksCompleted;
                if (tasksCompleted != null)
                {
                    // Execute event on the current thread
                    tasksCompleted(this, null);
                }
            }
            else
            {
                // Post the event on the creator thread
                _synchronizationContext.Post(new SendOrPostCallback(delegate(object state)
                {
                    var tasksCompleted = TasksCompleted;
                    if (tasksCompleted != null)
                    {

                        tasksCompleted(this, null);
                    }
                }), null);
            }
        }

        #endregion // Thread

        public void Add(Func<object> method, Action<object, Exception> callback)
        {
            if (method == null)
                throw new ArgumentNullException("method");
            if (callback == null)
                throw new ArgumentNullException("callback");
            if (!_initialized)
                throw new InvalidOperationException("Not initialized!");

            _requestQueue.Enqueue(new AsyncRequest() { Method = method, Callback = callback });
            _canProcess.Set();
        }

        private class AsyncRequest
        {
            public Func<object> Method { get; set; }
            public Action<object, Exception> Callback { get; set; }
            public object Result;
            public Exception Error { get; set; }
        }
    }
}
