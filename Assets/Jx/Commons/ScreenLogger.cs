using System;
using System.Collections.Concurrent;
using UnityEngine;
/********************************************************************
	created:	2024/05/08 [14:23]
	filename: 	ScreenLogger.cs
	author:		jzq
	purpose:	thread-safe (ConCurrentQueue)
*********************************************************************/
namespace AClockworkBerry
{
	public class ScreenLogger : MonoBehaviour
	{
		// 		#region Static
		// 		public static bool IsPersistent = true;
		// 		#endregion
		#region Inspectors
		public enum LogAnchor
		{
			TopLeft,
			TopRight,
			BottomLeft,
			BottomRight
		}
		public bool ShowLog = true;

		[Tooltip("Height of the log area as a percentage of the screen height")]
		[Range(0.3f, 1.0f)]
		public float Height = 0.8f;

		[Tooltip("Width of the log area as a percentage of the screen width")]
		[Range(0.3f, 1.0f)]
		public float Width = 0.6f;

		public int Margin = 20;

		public LogAnchor AnchorPosition = LogAnchor.TopLeft;

		public int FontSize = 20;

		[Range(0f, 01f)]
		public float BackgroundOpacity = 0.5f;
		public Color BackgroundColor = Color.black;

		public bool LogMessages = true;
		public bool LogWarnings = true;
		public bool LogErrors = true;

		public Color MessageColor = Color.white;
		public Color WarningColor = Color.yellow;
		public Color ErrorColor = new Color(1, 0.5f, 0.5f);

		public bool StackTraceMessages = false;
		public bool StackTraceWarnings = false;
		public bool StackTraceErrors = false;
		//  [1/6/2021 jzq]
		[SerializeField, Header("0 : No Limit")]
		int maxRowsLimit = 0;
		[SerializeField, Range(1, 10)]
		int maxRowsPreLog = 2;
		[SerializeField, Header("Not Works so far")]
		bool autoScroll = true;
		#endregion

		public bool behaviourActive
		{
			get => ShowLog;
			set => ShowLog = value;
		}

		#region Unitys
		void Awake()
		{
#if ForceSingleton
			ScreenLogger[] obj = GameObject.FindObjectsOfType<ScreenLogger>();

			if (obj.Length > 1)
			{
				Debug.Log("Destroying ScreenLogger, already exists...");

				destroying = true;

				Destroy(gameObject);
				return;
			}
#endif
			InitStyles();

			//if (IsPersistent)
			DontDestroyOnLoad(transform.root);
		}

		void OnEnable()
		{
			//queue = new ConcurrentQueue<LogMessage>();

			Application.logMessageReceivedThreaded += HandleLog;
		}

		void OnDisable()
		{
			// If destroyed because already exists, don't need to de-register callback
			if (destroying)
				return;

			Application.logMessageReceivedThreaded += HandleLog;
		}

		void Update()
		{
			if (!ShowLog)
				return;
			if (Input.GetKeyDown(KeyCode.End))
				ScrollToBottom();
		}
		void OnGUI()
		{
			if (!ShowLog)
				return;
			if (Event.current.type == EventType.Layout)
				cacheCount = queue.Count;
			var scrollViewRect = GetLogViewRect();
			if (minimized)
			{
				if (GUI.Button(scrollViewRect, ">Log"))
					minimized = false;
				return;
			}
			//crossButton
			if (GUI.Button(GetBtnRect(scrollViewRect), "X"))
			{
				if (Input.GetKey(KeyCode.LeftShift))
					ClearLog();
				else
					minimized = !minimized;
			}
			GUILayout.BeginArea(scrollViewRect, styleContainer);
			//
			scrollpos = GUILayout.BeginScrollView(scrollpos, styleContainer);
			var c = 0;
			foreach (var m in queue)
			{
				if (c++ >= cacheCount)
					break;
				switch (m.Type)
				{
					case LogType.Warning:
						styleText.normal.textColor = WarningColor;
						break;

					case LogType.Log:
						styleText.normal.textColor = MessageColor;
						break;

					case LogType.Assert:
					case LogType.Exception:
					case LogType.Error:
						styleText.normal.textColor = ErrorColor;
						break;

					default:
						styleText.normal.textColor = MessageColor;
						break;
				}

				GUILayout.Label(m.Message, styleText);
			}
			GUILayout.EndScrollView();
			GUILayout.EndArea();
			//  [1/6/2021 jzq]
			if (logDirty && Event.current.type == EventType.Layout)
			{
				if (autoScroll)
					ScrollToBottom();
				logDirty = false;
			}

			if (Input.touchCount > 0)
			{
				if (Event.current.type != EventType.Layout && Event.current.type != EventType.Repaint)
				{
					Vector2? tv = null;
					for (int i = 0; i < Input.touchCount; ++i)
					{
						var touch = Input.GetTouch(i);
						if (!scrollViewRect.Contains(touch.position))
							continue;
						if (touch.phase == TouchPhase.Moved)
						{
							if (tv == null)
								tv = touch.deltaPosition;
							else
								tv += touch.deltaPosition;
						}
					}
					if (tv != null)
					{
						var d = tv.Value;
						d.x = -d.x;
						scrollpos += d;
						Event.current.Use();
					}
				}
			}
			/*else*/
			if (scrollViewRect.Contains(Event.current.mousePosition))
			{
				if ((Event.current.button == 2 && Event.current.type == EventType.MouseDrag))
				{
					scrollpos += -Event.current.delta;
					Event.current.Use();
				}
			}
		}
		#endregion

		#region Pub
		public void ClearLog()
		{
			queue.Clear();
			cacheCount = 0;
		}
		public void HandleLog(string message, string stackTrace, LogType type)
		{
			if (type == LogType.Assert && !LogErrors)
				return;
			if (type == LogType.Error && !LogErrors)
				return;
			if (type == LogType.Exception && !LogErrors)
				return;
			if (type == LogType.Log && !LogMessages)
				return;
			if (type == LogType.Warning && !LogWarnings)
				return;

			//-jzq - maxRowsPreLog
			{
				int idx = -1;
				for (int i = 0; i < maxRowsPreLog; ++i)
				{
					var cur = message.IndexOf('\n', idx + 1);
					if (cur == -1)
						break;
					idx = cur;
				}
				if (idx > -1)
					message = message.Substring(0, idx);
			}
			//  [1/6/2021 jzq]
			string[] lines = message.Split('\n');
			GuardLimitEnqueueLog(lines.Length);
			for (int i = 0; i < lines.Length; ++i)
				queue.Enqueue(new LogMessage(lines[i], type));

			if (type == LogType.Assert && !StackTraceErrors)
				return;
			if (type == LogType.Error && !StackTraceErrors)
				return;
			if (type == LogType.Exception && !StackTraceErrors)
				return;
			if (type == LogType.Log && !StackTraceMessages)
				return;
			if (type == LogType.Warning && !StackTraceWarnings)
				return;

			if (stackTrace != null)
			{
				string[] trace = stackTrace.Split('\n');
				GuardLimitEnqueueLog(trace.Length);
				for (int i = 0; i < trace.Length; ++i)
				{
					var t = trace[i];
					if (t.Length != 0)
						queue.Enqueue(new LogMessage("  " + t, type));
				}
			}
			logDirty = true;
		}

		#endregion

		#region IMP
		int cacheCount;
		class LogMessage
		{
			public string Message;
			public LogType Type;

			public LogMessage(string msg, LogType type)
			{
				Message = msg;
				Type = type;
			}
		}

		ConcurrentQueue<LogMessage> queue = new ConcurrentQueue<LogMessage>();

		GUIStyle styleContainer, styleText;
		int padding = 5;

		private bool destroying = false;

		//  [5/16/2016 jzq]
		Vector2 scrollpos;
		bool logDirty;
		bool minimized;
		void InitStyles()
		{
			Texture2D back = new Texture2D(1, 1);
			BackgroundColor.a = BackgroundOpacity;
			back.SetPixel(0, 0, BackgroundColor);
			back.Apply();

			styleContainer = new GUIStyle();
			styleContainer.normal.background = back;
			styleContainer.wordWrap = false;
			styleContainer.padding = new RectOffset(padding, padding, padding, padding);

			styleText = new GUIStyle();
			styleText.fontSize = FontSize;
		}

		void ScrollToBottom()
		{
			// Force the scrollbar to the bottom position.
			scrollpos.y = Mathf.Infinity;
		}
		void GuardLimitEnqueueLog(int lineCnt)
		{
			if (maxRowsLimit > 0)
			{
				var exceed = queue.Count + lineCnt - (maxRowsLimit + 1);
				while (--exceed > 0)
				{
					if (!queue.TryDequeue(out var t))
						break;
				}
			}
		}
		//void InspectorGUIUpdated()
		//{
		//	InitStyles();
		//}
		Rect GetLogViewRect()
		{
			Vector2 logBtnSize = new Vector2(70, 40);

			float w = !minimized ? (Screen.width - 2 * Margin) * Width : logBtnSize.x;
			float h = !minimized ? (Screen.height - 2 * Margin) * Height : logBtnSize.y;
			float x = 1, y = 1;
			switch (AnchorPosition)
			{
				case LogAnchor.BottomLeft:
					if (!minimized)
					{
						x = Margin;
						y = Margin + (Screen.height - 2 * Margin) * (1 - Height);
					}
					else
					{
						x = Margin;
						y = Screen.height - Margin - h;
					}
					break;

				case LogAnchor.BottomRight:
					if (!minimized)
					{
						x = Margin + (Screen.width - 2 * Margin) * (1 - Width);
						y = Margin + (Screen.height - 2 * Margin) * (1 - Height);
					}
					else
					{
						x = Screen.width - Margin - w;
						y = Screen.height - Margin - h;
					}
					break;

				case LogAnchor.TopLeft:
					x = Margin;
					y = Margin;
					break;

				case LogAnchor.TopRight:
					if (!minimized)
					{
						x = Margin + (Screen.width - 2 * Margin) * (1 - Width);
						y = Margin;
					}
					else
					{
						x = Screen.width - Margin - w;
						y = Margin;
					}
					break;
			}
			return new Rect(x, y, w, h);
		}
		Rect GetBtnRect(Rect view)
		{
			Rect rec = Rect.zero;
			const int btnSize = 40;
			const int marginToView = 0;
			switch (AnchorPosition)
			{
				case LogAnchor.BottomLeft:
					rec = new Rect(view.xMax + marginToView, view.yMin - marginToView - btnSize, btnSize, btnSize);
					break;

				case LogAnchor.BottomRight:
					rec = new Rect(view.xMin - btnSize - marginToView, view.yMin - btnSize - marginToView, btnSize, btnSize);
					break;

				case LogAnchor.TopLeft:
					rec = new Rect(view.xMax + marginToView, view.yMax + marginToView, btnSize, btnSize);
					break;

				case LogAnchor.TopRight:
					rec = new Rect(view.xMin - btnSize - marginToView, view.yMax + marginToView, btnSize, btnSize);
					break;
			}
			return rec;
		}
		#endregion
	}
}

/*
The MIT License

Copyright © 2016 Screen Logger - Giuseppe Portelli <giuseppe@aclockworkberry.com>

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in
all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
THE SOFTWARE.
*/
