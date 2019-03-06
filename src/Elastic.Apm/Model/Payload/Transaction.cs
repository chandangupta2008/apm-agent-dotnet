﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Elastic.Apm.Api;
using Elastic.Apm.Helpers;
using Elastic.Apm.Logging;
using Elastic.Apm.Report;
using Newtonsoft.Json;

namespace Elastic.Apm.Model.Payload
{
	internal class Transaction : ITransaction
	{
		[JsonProperty("span_count")]
		public SpanCount SpanCount { get; set; }

		[JsonProperty("trace_id")]
		public string TraceId { get; set; }

		private readonly DateTimeOffset _start;

		private readonly Lazy<Context> _context = new Lazy<Context>();
		private readonly ScopedLogger _logger;
		private readonly IPayloadSender _sender;

		public Transaction(IApmAgent agent, string name, string type)
			: this(agent.Logger, name, type, agent.PayloadSender) { }

		public Transaction(IApmLogger logger, string name, string type, IPayloadSender sender)
		{
			_logger = logger?.Scoped(nameof(Transaction));
			_sender = sender;
			_start = DateTimeOffset.Now;

			Name = name;
			Type = type;
			Random rnd = new Random();
			Id = rnd.Next().ToString("x");
			TraceId = rnd.Next().ToString("x");

			SpanCount = new SpanCount(); //TODO
		}

		/// <summary>
		/// Any arbitrary contextual information regarding the event, captured by the agent, optionally provided by the user.
		/// </summary>
		public Context Context => _context.Value;

		/// <inheritdoc />
		/// <summary>
		/// The duration of the transaction.
		/// If it's not set (HasValue returns false) then the value
		/// is automatically calculated when <see cref="End" /> is called.
		/// </summary>
		/// <value>The duration.</value>
		public double? Duration { get; set; }

		public string Id { get; }

		public string Name { get; set; }

		/// <inheritdoc />
		/// <summary>
		/// A string describing the result of the transaction.
		/// This is typically the HTTP status code, or e.g. "success" for a background task.
		/// </summary>
		/// <value>The result.</value>
		public string Result { get; set; }

		internal Service Service;

		[JsonIgnore]
		public Dictionary<string, string> Tags => Context.Tags;

		public long Timestamp => _start.ToUnixTimeMilliseconds() * 1000;

		private static long ToUnixTime(DateTime date) //TODO: offset?
		{
			var epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
			return Convert.ToInt64((date - epoch).TotalSeconds);
		}

		public string Type { get; set; }

		public void End()
		{
			if (!Duration.HasValue) Duration = (DateTimeOffset.UtcNow - _start).TotalMilliseconds;

			_sender.QueueTransaction(this);

			_logger.LogDebug($"Ending Span: {Name}, {Type}");
			Agent.TransactionContainer.Transactions.Value = null;
		}

		public ISpan StartSpan(string name, string type, string subType = null, string action = null)
			=> StartSpanInternal(name, type, subType, action);

		internal Span StartSpanInternal(string name, string type, string subType = null, string action = null)
		{
			var retVal = new Span(name, type, this, _sender, _logger);

			if (!string.IsNullOrEmpty(subType)) retVal.Subtype = subType;

			if (!string.IsNullOrEmpty(action)) retVal.Action = action;

			var currentTime = DateTimeOffset.UtcNow;
			SpanCount.Started++;

			_logger.LogDebug($"Starting Span: Id:{Id}, TraceId:{TraceId} Name:{Name}, Type:{Type}");
			return retVal;
		}

		public void CaptureException(Exception exception, string culprit = null, bool isHandled = false , string parentId = null)
		{
			var capturedCulprit = string.IsNullOrEmpty(culprit) ? "PublicAPI-CaptureException" : culprit;

			var ed = new ExceptionDetails()
			{
				Message = exception.Message,
				Type = exception.GetType().FullName,
				Handled = isHandled,
			};

			if (!string.IsNullOrEmpty(exception.StackTrace))
			{
				ed.Stacktrace
					= StacktraceHelper.GenerateApmStackTrace(new StackTrace(exception, true).GetFrames(), _logger,
						"failed capturing stacktrace");
			}

			_sender.QueueError(new Error(ed, this.TraceId, this.Id, parentId ?? this.Id) { Culprit = capturedCulprit, Context = Context });
		}

		public void CaptureError(string message, string culprit, System.Diagnostics.StackFrame[] frames, string parentId = null)
		{

			var capturedCulprit = string.IsNullOrEmpty(culprit) ? "PublicAPI-CaptureException" : culprit;

			var ed = new ExceptionDetails()
			{
				Message = message,
			};

			if (frames != null)
			{
				ed.Stacktrace
					= StacktraceHelper.GenerateApmStackTrace(frames, _logger, "failed capturing stacktrace");
			}

			_sender.QueueError(new Error(ed, this.TraceId, this.Id, parentId ?? this.Id) { Culprit = capturedCulprit, Context = Context });
		}

		public void CaptureSpan(string name, string type, Action<ISpan> capturedAction, string subType = null, string action = null)
		{
			var span = StartSpan(name, type, subType, action);

			try
			{
				capturedAction(span);
			}
			catch (Exception e) when (ExceptionFilter.Capture(e, span)) { }
			finally
			{
				span.End();
			}
		}

		public void CaptureSpan(string name, string type, Action capturedAction, string subType = null, string action = null)
		{
			var span = StartSpan(name, type, subType, action);

			try
			{
				capturedAction();
			}
			catch (Exception e) when (ExceptionFilter.Capture(e, span)) { }
			finally
			{
				span.End();
			}
		}

		public T CaptureSpan<T>(string name, string type, Func<ISpan, T> func, string subType = null, string action = null)
		{
			var span = StartSpan(name, type, subType, action);
			var retVal = default(T);
			try
			{
				retVal = func(span);
			}
			catch (Exception e) when (ExceptionFilter.Capture(e, span)) { }
			finally
			{
				span.End();
			}

			return retVal;
		}

		public T CaptureSpan<T>(string name, string type, Func<T> func, string subType = null, string action = null)
		{
			var span = StartSpan(name, type, subType, action);
			var retVal = default(T);
			try
			{
				retVal = func();
			}
			catch (Exception e) when (ExceptionFilter.Capture(e, span)) { }
			finally
			{
				span.End();
			}

			return retVal;
		}

		public Task CaptureSpan(string name, string type, Func<Task> func, string subType = null, string action = null)
		{
			var span = StartSpan(name, type, subType, action);
			var task = func();
			RegisterContinuation(task, span);
			return task;
		}

		public Task CaptureSpan(string name, string type, Func<ISpan, Task> func, string subType = null, string action = null)
		{
			var span = StartSpan(name, type, subType, action);
			var task = func(span);
			RegisterContinuation(task, span);
			return task;
		}

		public Task<T> CaptureSpan<T>(string name, string type, Func<Task<T>> func, string subType = null, string action = null)
		{
			var span = StartSpan(name, type, subType, action);
			var task = func();
			RegisterContinuation(task, span);

			return task;
		}

		public Task<T> CaptureSpan<T>(string name, string type, Func<ISpan, Task<T>> func, string subType = null, string action = null)
		{
			var span = StartSpan(name, type, subType, action);
			var task = func(span);
			RegisterContinuation(task, span);
			return task;
		}

		/// <summary>
		/// Registers a continuation on the task.
		/// Within the continuation it ends the transaction and captures errors
		/// </summary>
		private static void RegisterContinuation(Task task, ISpan span) => task.ContinueWith(t =>
		{
			if (t.IsFaulted)
			{
				if (t.Exception != null)
				{
					if (t.Exception is AggregateException aggregateException)
					{
						ExceptionFilter.Capture(
							aggregateException.InnerExceptions.Count == 1
								? aggregateException.InnerExceptions[0]
								: aggregateException.Flatten(), span);
					}
					else
						ExceptionFilter.Capture(t.Exception, span);
				}
				else
					span.CaptureError("Task faulted", "A task faulted", new StackTrace(true).GetFrames());
			}
			else if (t.IsCanceled)
			{
				if (t.Exception == null)
				{
					span.CaptureError("Task canceled", "A task was canceled",
						new StackTrace(true).GetFrames()); //TODO: this async stacktrace is hard to use, make it readable!
				}
				else
					span.CaptureException(t.Exception);
			}

			span.End();
		}, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
	}
}
