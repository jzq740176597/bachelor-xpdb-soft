using UnityEngine;
using System.Collections.Generic;
using System.Linq;
// Simple code profiler class for Unity projects
// @robotduck 2011
//
// usage: place on an empty gameobject in your scene
// then insert calls to CodeProfiler.Begin(id) and
// CodeProfiler.End(id) around the section you want to profile
//
// "id" should be string, unique to each code portion that you're timing
// for example, in your enemy update function, you might have:
//
//............
//
// the Begin id and the End id must match exactly.

/*

	**********-by jzq *************

public class ProfileTest : MonoBehaviour {

	// Use this for initialization
	void Start () {
		InvokeRepeating("OneShotTest" , 0 , 2);
	}

	// Update is called once per frame
	void Update () {
		PreFrameTest();
	}
	void OneShotTest()
	{
		CodeProfiler.Begin("DoSthInOneShot" , ProfileType.OneShot);
		_DoSthInOneShot();
		CodeProfiler.End();
	}
	void PreFrameTest()
	{
		CodeProfiler.Begin("DoSthInPreFrame" , ProfileType.PreFrame);
		_DoSthInPreFrame();
		CodeProfiler.End();
	}
}*/
public class CodeProfiler : MonoBehaviour
{
	#region Inspector
	[SerializeField, Range(1, 10)]
	int outputSampleInterval = 2;
	#endregion

	#region Imp
	float startTime = 0;
	float nextOutputTime = 2;
	int numFrames = 0;
	string displayText;
	Rect displayRect = new Rect(10, 10, 460, 300);
	void ResetProfile()
	{
		numFrames = 0;
		startTime = Time.time;
		nextOutputTime = startTime + outputSampleInterval;
	}

	#endregion
	#region Unity
	void Awake()
	{
		DontDestroyOnLoad(transform.root.gameObject);
		ResetProfile();
		displayText = "\n\nTaking initial readings...";
	}
	void OnGUI()
	{
		try
		{
			GUI.Box(displayRect, "Code Profiler");
			GUI.Label(displayRect, displayText);
		}
		catch (System.Exception ex)
		{
			Debug.LogError(ex.Message);
		}
	}
	void Update()
	{
		if (Input.GetKeyDown(KeyCode.P))
		{
			Debug.Log("ProfileLog : " + displayText);
		}
		numFrames++;

		if (Time.time > nextOutputTime)
		{
			// time to display the results      

			displayText = CollectOutputText_S(startTime, numFrames);
			//Debug.Log(displayText);

			// reset & schedule the next time to display results:
			ResetProfile();
		}
	}
	#endregion
	#region Static
	static Dictionary<string, ProfilerRecording> recordings_s = new Dictionary<string, ProfilerRecording>();

	static Stack<string> pendingIdStack_s = new Stack<string>();
	public static void Begin(string id, ProfileType type = ProfileType.OneShot)
	{
		// create a new recording if not present in the list
		if (!recordings_s.ContainsKey(id))
		{
			recordings_s[id] = new ProfilerRecording(id, type);
		}
		recordings_s[id].Start();
		pendingIdStack_s.Push(id);
	}

	public static void End()
	{
		if (pendingIdStack_s.Count == 0)
		{
			Debug.Log("[Error] CodeProfiler.End() while pendingIdStack is Empty!");
			return;
		}
		var id = pendingIdStack_s.Pop();
		recordings_s[id].Stop();
	}
	//  [2/17/2023 jzq]
	public static string CollectOutputText_S(float? lastPlayTime = null, float? lastPlayFrames = null)
	{
		if (Application.isPlaying)
		{
			if (lastPlayTime == null || lastPlayFrames == null)
			{
				Debug.Log("lastPlayTime should pass-value on playing!".WrapLogColor());
				//return string.Empty;
			}
		}
		else
		{
			lastPlayTime = null;
		}
		bool playData = lastPlayTime != null && lastPlayFrames != null;
		//  [2/28/2018 jzq]
		var preFrameSet = recordings_s.Values.Where(e => e.profileType == ProfileType.PreFrame);
		var oneShotSet = recordings_s.Values.Except(preFrameSet);

		// column width for text display
		const int colWidth = 10;
		float totalMS = playData ? (Time.time - lastPlayTime.Value) * 1000 : 0;
		//Has entry
		var displayText = string.Empty;
		if (playData)
		{
			if (recordings_s.Values.Count > 0)
			{
				// the overall frame time and frames per second:
				displayText = "\n";
				float avgMS = (totalMS / lastPlayFrames.Value);
				float fps = (1000 / (totalMS / lastPlayFrames.Value));
				displayText += "Avg frame time: ";
				displayText += avgMS.ToString("0.#") + "ms, ";
				displayText += fps.ToString("0.#") + " fps \n";
			}
			if (preFrameSet.Count() > 0)
			{
				// the column titles for the individual recordings:
				displayText += "\nTotal".PadRight(colWidth);
				displayText += "MS/frame".PadRight(colWidth);
				displayText += "Calls/frame".PadRight(colWidth);
				displayText += "MS/call".PadRight(colWidth);
				displayText += "Id";
				displayText += "\n";

				// now we loop through each individual recording
				foreach (var recording in preFrameSet)
				{
					// Each "entry" is a key-value pair where the string ID
					// is the key, and the recording instance is the value:
					//ProfilerRecording recording = entry;

					// calculate the statistics for this recording:
					float recordedMS = (recording.Seconds * 1000);
					float percent = (recordedMS * 100) / totalMS;
					float msPerFrame = recordedMS / lastPlayFrames.Value;
					float msPerCall = recordedMS / recording.Count;
					float timesPerFrame = recording.Count / (float) lastPlayFrames.Value;

					// add the stats to the display text
					displayText += (percent.ToString("0.000") + "%").PadRight(colWidth);
					displayText += (msPerFrame.ToString("0.000") + "ms").PadRight(colWidth);
					displayText += (timesPerFrame.ToString("0.0")).PadRight(colWidth);
					displayText += (msPerCall.ToString("0.0000") + "ms").PadRight(colWidth);
					displayText += (recording.Id);
					displayText += "\n";

					// and reset the recording
					recording.Reset();
				}
			}
		}
		if (oneShotSet.Count() > 0)
		{
			// the column titles for the individual recordings:
			displayText += "\nMS".PadRight(colWidth);
			displayText += "Calls".PadRight(colWidth);
			displayText += "MS/call".PadRight(colWidth);
			displayText += "Id";
			displayText += "\n";

			foreach (var recording in oneShotSet)
			{
				// add the stats to the display text
				displayText += ((recording.Seconds * 1000).ToString("0.000")).PadRight(colWidth);
				displayText += (recording.Count.ToString().PadRight(colWidth));
				displayText += ((recording.Seconds * 1000 / recording.Count).ToString("0.000")).PadRight(colWidth);
				displayText += (recording.Id);
				displayText += "\n";

				//No -Reset for OneShot Record
				// and reset the recording
				//recording.Reset();

				// clear on !PlayData [2/17/2023 jzq]
				if (!playData)
					recording.Reset();
			}
		}
		return displayText;
	}
	#endregion

}


// this is the ProfileRecording class which is simply included
// directly after the CodeProfiler class in the same file.
// The ProfileRecording class is basically for "internal use
// only" - you don't need to place it on a gameobject or interact
// with it in any way yourself, it's purely used by the
// CodeProfiler to do its job.
//  [2/28/2018 jzq]
public enum ProfileType
{
	OneShot,
	PreFrame,
}
class ProfilerRecording
{
	public string Id
	{
		get
		{
			return id;
		}
	}
	// this class accumulates time for a single recording

	int count = 0;
	float startTime = 0;
	float accumulatedTime = 0;
	bool started = false;
	string id;

	public ProfileType profileType;

	public ProfilerRecording(string id, ProfileType type)
	{
		this.id = id;
		this.profileType = type;
	}

	public void Start()
	{
		if (started)
		{
			BalanceError();
		}
		count++;
		started = true;
		startTime = Time.realtimeSinceStartup; // done last
	}

	public void Stop()
	{
		float endTime = Time.realtimeSinceStartup; // done first
		if (!started)
		{
			BalanceError();
		}
		started = false;
		float elapsedTime = (endTime - startTime);
		accumulatedTime += elapsedTime;
	}

	public void Reset()
	{
		accumulatedTime = 0;
		count = 0;
		started = false;
	}

	void BalanceError()
	{
		// this lets you know if you've accidentally
		// used the begin/end functions out of order
		Debug.LogError("ProfilerRecording start/stops not balanced for '" + id + "'");
	}

	public float Seconds
	{
		get
		{
			return accumulatedTime;
		}
	}

	public int Count
	{
		get
		{
			return count;
		}
	}

}