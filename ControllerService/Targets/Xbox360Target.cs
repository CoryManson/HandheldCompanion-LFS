﻿using ControllerCommon;
using Microsoft.Extensions.Logging;
using Nefarius.ViGEm.Client;
using Nefarius.ViGEm.Client.Targets;
using Nefarius.ViGEm.Client.Targets.Xbox360;
using SharpDX.XInput;
using System.Collections.Generic;
using System.Timers;
using GamepadButtonFlags = SharpDX.XInput.GamepadButtonFlags;

namespace ControllerService.Targets
{
    internal partial class Xbox360Target : ViGEmTarget
    {
        private static readonly List<Xbox360Button> ButtonMap = new List<Xbox360Button>
        {
            Xbox360Button.Up,
            Xbox360Button.Down,
            Xbox360Button.Left,
            Xbox360Button.Right,
            Xbox360Button.Start,
            Xbox360Button.Back,
            Xbox360Button.LeftThumb,
            Xbox360Button.RightThumb,
            Xbox360Button.LeftShoulder,
            Xbox360Button.RightShoulder,
            Xbox360Button.Guide,
            Xbox360Button.A,
            Xbox360Button.B,
            Xbox360Button.X,
            Xbox360Button.Y
        };

        private static readonly List<Xbox360Axis> AxisMap = new List<Xbox360Axis>
        {
            Xbox360Axis.LeftThumbX,
            Xbox360Axis.LeftThumbY,
            Xbox360Axis.RightThumbX,
            Xbox360Axis.RightThumbY
        };

        private static readonly List<Xbox360Slider> SliderMap = new List<Xbox360Slider>
        {
            Xbox360Slider.LeftTrigger,
            Xbox360Slider.RightTrigger
        };

        private new IXbox360Controller vcontroller;

        public Xbox360Target(XInputController xinput, ViGEmClient client, Controller controller, int index, int HIDrate, ILogger logger) : base(xinput, client, controller, index, HIDrate, logger)
        {
            // initialize controller
            HID = HIDmode.Xbox360Controller;

            vcontroller = client.CreateXbox360Controller();
            vcontroller.AutoSubmitReport = false;
            vcontroller.FeedbackReceived += FeedbackReceived;

            // initialize timers
            UpdateTimer.Elapsed += UpdateReport;
        }

        public override void Connect()
        {
            vcontroller.Connect();
            base.Connect();
        }

        public override void Disconnect()
        {
            vcontroller.Disconnect();
            base.Disconnect();
        }

        public void FeedbackReceived(object sender, Xbox360FeedbackReceivedEventArgs e)
        {
            if (!Controller.IsConnected)
                return;

            Vibration inputMotor = new()
            {
                LeftMotorSpeed = (ushort)((e.LargeMotor * ushort.MaxValue / byte.MaxValue) * strength),
                RightMotorSpeed = (ushort)((e.SmallMotor * ushort.MaxValue / byte.MaxValue) * strength),
            };
            Controller.SetVibration(inputMotor);
        }

        public override unsafe void UpdateReport(object sender, ElapsedEventArgs e)
        {
            lock (updateLock)
            {
                if (!Controller.IsConnected)
                    return;

                if (xinput.Profile.whitelisted)
                    return;

                base.UpdateReport(sender, e);

                vcontroller.SetAxisValue(Xbox360Axis.LeftThumbX, LeftThumbX);
                vcontroller.SetAxisValue(Xbox360Axis.LeftThumbY, LeftThumbY);
                vcontroller.SetAxisValue(Xbox360Axis.RightThumbX, RightThumbX);
                vcontroller.SetAxisValue(Xbox360Axis.RightThumbY, RightThumbY);

                foreach (Xbox360Button button in ButtonMap)
                {
                    GamepadButtonFlags value = (GamepadButtonFlags)button.Value;
                    vcontroller.SetButtonState(button, Gamepad.Buttons.HasFlag(value));
                }

                vcontroller.SetSliderValue(Xbox360Slider.LeftTrigger, Gamepad.LeftTrigger);
                vcontroller.SetSliderValue(Xbox360Slider.RightTrigger, Gamepad.RightTrigger);

                vcontroller.SubmitReport();

                base.SubmitReport();
            }
        }
    }
}
