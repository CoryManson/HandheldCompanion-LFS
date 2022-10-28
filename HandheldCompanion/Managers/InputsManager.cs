using ControllerCommon;
using ControllerCommon.Devices;
using ControllerCommon.Managers;
using Gma.System.MouseKeyHook;
using GregsStack.InputSimulatorStandard;
using GregsStack.InputSimulatorStandard.Native;
using HandheldCompanion.Managers.Classes;
using HandheldCompanion.Views;
using PrecisionTiming;
using SharpDX.XInput;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using WindowsInput.Events;

namespace HandheldCompanion.Managers
{
    public static class InputsManager
    {
        // Gamepad variables
        private static ControllerEx controllerEx;
        private static Gamepad Gamepad;
        private static Gamepad prevGamepad;
        private static State GamepadState;
        private static PrecisionTimer UpdateTimer;
        private static PrecisionTimer ResetTimer;

        // InputsChord variables
        private static InputsChord currentChord = new();
        private static InputsChord prevChord = new();

        private static PrecisionTimer InputsChordHoldTimer;
        private static PrecisionTimer InputsChordInputTimer;

        private static bool ExpectKeyDown = true;
        private static bool ExpectKeyUp = false;
        private static Dictionary<KeyValuePair<KeyCode, bool>, int> prevKeys = new();

        // Global variables
        private static PrecisionTimer ListenerTimer;

        private const short TIME_RELEASE = 10;      // default interval between gamepad updates
        private const short TIME_FLUSH = 20;        // default interval between buffer flush
        private const short TIME_FLUSH_EXT = 100;   // extended buffer flush interval when expecting another chord key

        private const short TIME_NEXT = 500;        // default interval before submitting output keys used in combo
        private const short TIME_LONG = 800;        // default interval between two inputs from a chord
                                                    // default interval before considering a chord as hold

        private const short TIME_EXPIRED = 3000;    // default interval before considering a chord as expired if no input is detected

        private static bool IsLocked;
        private static bool IsCombo;
        private static InputsHotkey currentHotkey = new();

        private static List<KeyEventArgsExt> ReleasedKeys = new();
        private static List<KeyEventArgsExt> CapturedKeys = new();

        private static Dictionary<string, InputsChord> Triggers = new();

        // Keyboard vars
        private static IKeyboardMouseEvents m_GlobalHook;
        private static InputSimulator m_InputSimulator;

        public static event UpdatedEventHandler Updated;
        public delegate void UpdatedEventHandler(Gamepad gamepad);

        public static event TriggerRaisedEventHandler TriggerRaised;
        public delegate void TriggerRaisedEventHandler(string listener, InputsChord inputs);

        public static event TriggerUpdatedEventHandler TriggerUpdated;
        public delegate void TriggerUpdatedEventHandler(string listener, InputsChord inputs, bool IsCombo);

        private static short KeyIndex;
        private static bool KeyUsed;

        public static bool IsInitialized;

        /*
         * InputsManager v3
         * Note: I'd like to modify the InputSimulator library to extend its capacities and ModifiedKeyDown and ModifiedKeyUp
         *       https://github.com/GregsStack/InputSimulatorStandard
         */

        static InputsManager()
        {
            // initialize timers
            UpdateTimer = new PrecisionTimer();
            UpdateTimer.SetInterval(TIME_RELEASE);
            UpdateTimer.SetAutoResetMode(true);

            UpdateTimer.Tick += (sender, e) => UpdateReport();

            ResetTimer = new PrecisionTimer();
            ResetTimer.SetInterval(TIME_FLUSH);
            ResetTimer.SetAutoResetMode(false);

            ResetTimer.Tick += (sender, e) => ReleaseBuffer();

            ListenerTimer = new PrecisionTimer();
            ListenerTimer.SetInterval(TIME_EXPIRED);
            ListenerTimer.SetAutoResetMode(false);

            ListenerTimer.Tick += (sender, e) => ListenerExpired();

            InputsChordHoldTimer = new PrecisionTimer();
            InputsChordHoldTimer.SetInterval(TIME_LONG);
            InputsChordHoldTimer.SetAutoResetMode(false);

            InputsChordHoldTimer.Tick += (sender, e) => InputsChordHold_Elapsed();

            InputsChordInputTimer = new PrecisionTimer();
            InputsChordInputTimer.SetInterval(TIME_NEXT);
            InputsChordInputTimer.SetAutoResetMode(false);

            InputsChordInputTimer.Tick += (sender, e) => { ExecuteSequence(); };

            m_GlobalHook = Hook.GlobalEvents();
            m_InputSimulator = new InputSimulator();

            HotkeysManager.HotkeyCreated += TriggerCreated;

            Thread.CurrentThread.Priority = ThreadPriority.Highest;
        }

        public static void UpdateController(ControllerEx _controllerEx)
        {
            controllerEx = _controllerEx;
        }

        private static void InputsChordHold_Elapsed()
        {
            // triggered when key is pressed for a long time
            currentChord.InputsType = InputsChordType.Hold;

            // we're no-longer expecting a KeyUp call
            ExpectKeyUp = false;

            ExecuteSequence();
        }

        private static void ExecuteSequence()
        {
            InputsChordHoldTimer.Stop();
            InputsChordInputTimer.Stop();

            if (string.IsNullOrEmpty(currentChord.SpecialKey) &&
                currentChord.GamepadButtons == GamepadButtonFlags.None &&
                currentChord.OutputKeys.Count == 0)
                return;

            if (string.IsNullOrEmpty(currentHotkey.Listener))
            {
                string key = GetTriggerFromChord(currentChord);

                if (!string.IsNullOrEmpty(key))
                {
                    LogManager.LogDebug("Captured: KeyDown: {0}, ButtonFlags: {1}, Type: {2}", currentChord.SpecialKey, currentChord.GamepadButtons, currentChord.InputsType);

                    ReleasedKeys.Clear();

                    InputsChord chord = Triggers[key];
                    TriggerRaised?.Invoke(key, chord);
                }
                else
                {
                    LogManager.LogDebug("Released: KeyDown: {0}, ButtonFlags: {1}, Type: {2}", currentChord.SpecialKey, currentChord.GamepadButtons, currentChord.InputsType);

                    ReleasedKeys.AddRange(CapturedKeys);
                    ResetTimer.Start();
                }

                CapturedKeys.Clear();
            }
            else
                StopListening(currentChord);

            // reset chord
            currentChord = new();

            // reset index
            KeyIndex = 0;
        }

        private static List<KeyEventArgsExt> InjectModifiers(KeyEventArgsExt args)
        {
            List<KeyEventArgsExt> mods = new();

            if (args.Modifiers == Keys.None)
                return mods;

            foreach (Keys mode in ((Keys[])Enum.GetValues(typeof(Keys))).Where(a => a != Keys.None))
            {
                if (args.Modifiers.HasFlag(mode))
                {
                    KeyEventArgsExt mod = new KeyEventArgsExt(mode, args.ScanCode, args.Timestamp, args.IsKeyDown, args.IsKeyUp, true);
                    mods.Add(mod);
                }
            }

            return mods;
        }

        private static void M_GlobalHook_KeyEvent(object? sender, KeyEventArgs e)
        {
            if (IsLocked)
                return;

            ResetTimer.Stop();
            KeyUsed = false;

            KeyEventArgsExt args = (KeyEventArgsExt)e;
            KeyCode hookKey = (KeyCode)args.KeyValue;

#if DEBUG
            LogManager.LogDebug("{1}\tKeyEvent: {0}, IsKeyDown: {2}, IsKeyUp: {3}", hookKey, args.Timestamp, args.IsKeyDown, args.IsKeyUp);
#endif

            // are we listening for keyboards inputs as part of a custom hotkey ?
            if (IsCombo)
            {
                args.SuppressKeyPress = true;
                if (args.IsKeyUp && args.IsExtendedKey)
                    CapturedKeys.AddRange(InjectModifiers(args));

                // add key to InputsChord
                currentChord.AddKey(args);

                InputsChordInputTimer.Stop();
                InputsChordInputTimer.Start();

                return;
            }

            foreach (DeviceChord pair in MainWindow.handheldDevice.listeners)
            {
                List<KeyCode> chord = pair.chords[args.IsKeyDown];
                if (KeyIndex >= chord.Count)
                    continue;

                KeyCode chordKey = chord[KeyIndex];
                if (chordKey == hookKey)
                {
                    KeyUsed = true;
                    KeyIndex++;

                    // increase interval as we're expecting a new chord key
                    ResetTimer.SetInterval(TIME_FLUSH_EXT);

                    break; // leave loop
                }
                else
                {
                    // restore default interval
                    ResetTimer.SetInterval(TIME_FLUSH);
                }
            }

            // if key is used or previous key was, we need to maintain key(s) order
            if (KeyUsed || KeyIndex > 0)
            {
                args.SuppressKeyPress = true;

                // add key to buffer
                ReleasedKeys.Add(args);

                if (args.IsKeyUp && args.IsExtendedKey)
                    ReleasedKeys.AddRange(InjectModifiers(args));

                // search for matching triggers
                List<KeyCode> buffer_keys = GetBufferKeys().OrderBy(key => key).ToList();

                foreach (DeviceChord chord in MainWindow.handheldDevice.listeners)
                {
                    // compare ordered enumerable
                    List<KeyCode> chord_keys = chord.chords[args.IsKeyDown].OrderBy(key => key).ToList();

                    if (chord_keys.SequenceEqual(buffer_keys))
                    {
                        // check if inputs timestamp are too close from one to another
                        bool IsKeyUnexpected = false;

                        if (args.IsKeyDown)
                        {
                            if (!ExpectKeyUp)
                            {
                                ExpectKeyDown = false;
                                ExpectKeyUp = true;
                            }
                            else
                                IsKeyUnexpected = true;
                        }
                        else if (args.IsKeyUp)
                        {
                            if (!ExpectKeyUp)
                                IsKeyUnexpected = true;
                            else
                            {
                                ExpectKeyDown = true;
                                ExpectKeyUp = false;
                            }
                        }

                        var pair = new KeyValuePair<KeyCode, bool>(hookKey, args.IsKeyDown);
                        var prevTimestamp = prevKeys.ContainsKey(pair) ? prevKeys[pair] : TIME_FLUSH_EXT;
                        prevKeys[pair] = args.Timestamp;

                        // spamming
                        if (args.Timestamp - prevTimestamp < TIME_FLUSH_EXT)
                            IsKeyUnexpected = true;

                        // only intercept inputs if not too close
                        if (!IsKeyUnexpected)
                            CapturedKeys.AddRange(ReleasedKeys);

                        // clear buffer
                        ReleasedKeys.Clear();

                        // leave if inputs are too close
                        if (IsKeyUnexpected)
                            return;

                        LogManager.LogDebug("{1}\tKeyEvent: {0}, IsKeyDown: {2}, IsKeyUp: {3}", chord.name, args.Timestamp, args.IsKeyDown, args.IsKeyUp);

                        if (args.IsKeyDown)
                        {
                            InputsChordHoldTimer.Stop();
                            InputsChordHoldTimer.Start();

                            // update vars
                            currentChord.SpecialKey = chord.name;
                            currentChord.InputsType = InputsChordType.Click;
                        }
                        // Sequence was intercepted already
                        else if (args.IsKeyUp)
                        {
                            if (InputsChordHoldTimer.IsRunning())
                                ExecuteSequence();
                        }

                        return; // prevent multiple shortcuts from being triggered
                    }
                }
            }

            ResetTimer.Start();
        }

        private static string GetTriggerFromName(string KeyDownListener)
        {
            foreach (var pair in Triggers)
                if (pair.Value.SpecialKey == KeyDownListener)
                    return pair.Key;

            return string.Empty;
        }

        private static string GetTriggerFromChord(InputsChord chord)
        {
            foreach (var pair in Triggers)
                if (pair.Value.SpecialKey == chord.SpecialKey &&
                    pair.Value.InputsType == chord.InputsType &&
                    pair.Value.GamepadButtons == chord.GamepadButtons)
                    return pair.Key;

            return string.Empty;
        }

        public static void KeyPress(VirtualKeyCode key)
        {
            IsLocked = true;

            m_InputSimulator.Keyboard.KeyPress(key);

            IsLocked = false;
        }

        public static void KeyPress(VirtualKeyCode[] keys)
        {
            IsLocked = true;

            foreach (VirtualKeyCode key in keys)
                m_InputSimulator.Keyboard.KeyDown(key);

            foreach (VirtualKeyCode key in keys)
                m_InputSimulator.Keyboard.KeyUp(key);

            IsLocked = false;
        }

        public static void KeyPress(List<OutputKey> keys)
        {
            IsLocked = true;

            foreach (OutputKey key in keys)
            {
                if (key.IsKeyDown)
                    m_InputSimulator.Keyboard.KeyDown((VirtualKeyCode)key.KeyValue);
                else
                    m_InputSimulator.Keyboard.KeyUp((VirtualKeyCode)key.KeyValue);
            }

            IsLocked = false;
        }

        public static void KeyStroke(VirtualKeyCode mod, VirtualKeyCode key)
        {
            m_InputSimulator.Keyboard.ModifiedKeyStroke(mod, key);
        }

        private static void ReleaseBuffer()
        {
            if (ReleasedKeys.Count == 0)
                return;

            // reset index
            KeyIndex = 0;

            try
            {
                IsLocked = true;

                for (int i = 0; i < ReleasedKeys.Count; i++)
                {
                    KeyEventArgsExt args = ReleasedKeys[i];

                    // improve me
                    VirtualKeyCode key = (VirtualKeyCode)args.KeyValue;
                    if (args.KeyValue == 0)
                    {
                        if (args.Control)
                            key = VirtualKeyCode.LCONTROL;
                        else if (args.Alt)
                            key = VirtualKeyCode.RMENU;
                        else if (args.Shift)
                            key = VirtualKeyCode.RSHIFT;
                    }

                    switch (args.IsKeyDown)
                    {
                        case true:
                            m_InputSimulator.Keyboard.KeyDown(key);
                            break;
                        case false:
                            m_InputSimulator.Keyboard.KeyUp(key);
                            break;
                    }

                    // send after initial delay
                    int Timestamp = ReleasedKeys[i].Timestamp;
                    int n_Timestamp = Timestamp;

                    if (i + 1 < ReleasedKeys.Count)
                        n_Timestamp = ReleasedKeys[i + 1].Timestamp;

                    int d_Timestamp = n_Timestamp - Timestamp;
                    m_InputSimulator.Keyboard.Sleep(d_Timestamp);
                }
            }
            catch (Exception)
            {
            }

            // release lock
            IsLocked = false;

            // clear buffer
            ReleasedKeys.Clear();
        }

        private static List<KeyCode> GetBufferKeys()
        {
            List<KeyCode> keys = new List<KeyCode>();

            foreach (KeyEventArgsExt e in ReleasedKeys)
                keys.Add((KeyCode)e.KeyValue);

            return keys;
        }

        public static void Start()
        {
            if (IsInitialized)
                return;

            UpdateTimer.Start();

            m_GlobalHook.KeyDown += M_GlobalHook_KeyEvent;
            m_GlobalHook.KeyUp += M_GlobalHook_KeyEvent;

            IsInitialized = true;
        }

        public static void Stop()
        {
            if (!IsInitialized)
                return;

            UpdateTimer.Stop();

            //It is recommened to dispose it
            m_GlobalHook.KeyDown -= M_GlobalHook_KeyEvent;
            m_GlobalHook.KeyUp -= M_GlobalHook_KeyEvent;

            IsInitialized = false;
        }

        private static bool GamepadClearPending;
        private static void UpdateReport()
        {
            // get current gamepad state
            if (controllerEx != null && controllerEx.IsConnected())
            {
                GamepadState = controllerEx.GetState();
                Gamepad = GamepadState.Gamepad;
            }

            if (prevGamepad.GetHashCode() == Gamepad.GetHashCode())
                return;

            // IsKeyDown
            if (Gamepad.Buttons != 0)
            {
                if (GamepadClearPending)
                {
                    currentChord.GamepadButtons = Gamepad.Buttons;
                    GamepadClearPending = false;
                }
                else
                    currentChord.GamepadButtons |= Gamepad.Buttons;

                InputsChordHoldTimer.Stop();
                InputsChordHoldTimer.Start();
            }
            // IsKeyUp
            else if (Gamepad.Buttons == 0 && currentChord.GamepadButtons != GamepadButtonFlags.None)
            {
                GamepadClearPending = true;

                // Sequence was intercepted already
                if (InputsChordHoldTimer.IsRunning())
                    ExecuteSequence();
            }

            Updated?.Invoke(Gamepad);
            prevGamepad = Gamepad;
        }

        public static void StartListening(Hotkey hotkey, bool IsCombo)
        {
            // force expiration on previous listener, if any
            if (!string.IsNullOrEmpty(currentHotkey.Listener))
                ListenerExpired();

            currentHotkey = hotkey.inputsHotkey;
            currentChord = hotkey.inputsChord;
            prevChord = new InputsChord(currentChord.GamepadButtons, currentChord.SpecialKey, currentChord.OutputKeys, currentChord.InputsType);

            switch (IsCombo)
            {
                case true:
                    currentChord.OutputKeys.Clear();
                    break;
                default:
                case false:
                    currentChord.GamepadButtons = GamepadButtonFlags.None;
                    currentChord.SpecialKey = string.Empty;
                    break;
            }

            InputsManager.IsCombo = IsCombo;

            ReleasedKeys = new();

            ListenerTimer.Start();
        }

        private static void StopListening(InputsChord inputsChord = null)
        {
            if (inputsChord == null)
                inputsChord = new InputsChord();

            Triggers[currentHotkey.Listener] = new InputsChord(inputsChord.GamepadButtons, inputsChord.SpecialKey, inputsChord.OutputKeys, inputsChord.InputsType);
            TriggerUpdated?.Invoke(currentHotkey.Listener, inputsChord, IsCombo);

            LogManager.LogDebug("Trigger: {0} updated. key: {1}, buttons: {2}, type: {3}", currentHotkey.Listener, inputsChord.SpecialKey, inputsChord.GamepadButtons, inputsChord.InputsType);

            currentHotkey = new();
            currentChord = new();
            IsCombo = false;

            ListenerTimer.Stop();
            InputsChordHoldTimer.Stop();
            InputsChordInputTimer.Stop();
        }

        private static void ListenerExpired()
        {
            // restore previous chord
            StopListening(prevChord);
        }

        public static void ClearListening(Hotkey hotkey)
        {
            currentHotkey = hotkey.inputsHotkey;
            StopListening();
        }

        private static void TriggerCreated(Hotkey hotkey)
        {
            string listener = hotkey.inputsHotkey.Listener;

            Triggers.Add(listener, hotkey.inputsChord);
        }

        internal static void InvokeTrigger(Hotkey hotkey)
        {
            TriggerRaised?.Invoke(hotkey.inputsHotkey.Listener, hotkey.inputsChord);
        }
    }
}