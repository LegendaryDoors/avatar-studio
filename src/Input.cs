// One view of gamepad, keyboard and mouse, in the console's terms (A, B, LB / RB, LT / RT).

using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;

namespace AvatarStudio
{
    sealed class Input
    {
        private GamePadState pad, padPrev;
        private KeyboardState kb, kbPrev;
        private MouseState mouse, mousePrev;
        private float repeatTimer;
        private int repeatDir;                          // -1/1 with the axis packed: 1=left 2=right 3=up 4=down
        private const float RepeatDelay = 0.42f, RepeatRate = 0.11f;

        public Vector2 MousePos { get; private set; }   // in 1280x720 design space
        public bool MouseMoved { get; private set; }
        public bool MouseClicked { get; private set; }  // left button edge
        public bool MouseRightClicked { get; private set; }
        public bool MouseHeld { get; private set; }     // the left button down this frame (a drag)
        public int Wheel { get; private set; }          // notches this frame, +up
        public bool Left, Right, Up, Down;              // edges with key-repeat
        public bool Select, Back, PageLeft, PageRight, Y, X, Start;
        /// <summary>Trigger bytes (0..255) for the turntable: each pad in turn, as the editor reads them (sub_920CA618).</summary>
        public int TriggerLeft, TriggerRight;
        public float SpinInput { get { return (TriggerLeft - TriggerRight) * 0.00390625f; } }   // 0x92006F24
        /// <summary>The right stick in XINPUT units (y up), read in the same loop.</summary>
        public Vector2 RightStick;
        public bool StickMoved;
        public bool AnyEdge { get { return Left || Right || Up || Down || Select || Back || PageLeft || PageRight || Y || X; } }

        private readonly Func<Point> windowSize;

        public Input(Func<Point> windowSize) { this.windowSize = windowSize; }

        public void Update(float dt)
        {
            padPrev = pad; kbPrev = kb; mousePrev = mouse;
            pad = GamePad.GetState(PlayerIndex.One);
            kb = Keyboard.GetState();
            mouse = Mouse.GetState();

            Point win = windowSize();
            float sx = win.X > 0 ? Layout.W / (float)win.X : 1f, sy = win.Y > 0 ? Layout.H / (float)win.Y : 1f;
            Vector2 mp = new Vector2(mouse.X * sx, mouse.Y * sy);
            MouseMoved = mp != MousePos && (mouse.X != mousePrev.X || mouse.Y != mousePrev.Y);
            MousePos = mp;
            MouseClicked = mouse.LeftButton == ButtonState.Pressed && mousePrev.LeftButton == ButtonState.Released;
            MouseHeld = mouse.LeftButton == ButtonState.Pressed;
            MouseRightClicked = mouse.RightButton == ButtonState.Pressed && mousePrev.RightButton == ButtonState.Released;
            Wheel = (mouse.ScrollWheelValue - mousePrev.ScrollWheelValue) / 120;

            bool l = PadDir(-1, 0) || KeyDown(Keys.Left);
            bool r = PadDir(1, 0) || KeyDown(Keys.Right);
            bool u = PadDir(0, 1) || KeyDown(Keys.Up);
            bool d = PadDir(0, -1) || KeyDown(Keys.Down);
            int dir = l ? 1 : r ? 2 : u ? 3 : d ? 4 : 0;
            Left = Right = Up = Down = false;
            if (dir != 0)
            {
                bool fire = false;
                if (dir != repeatDir) { repeatDir = dir; repeatTimer = RepeatDelay; fire = true; }
                else { repeatTimer -= dt; if (repeatTimer <= 0) { repeatTimer = RepeatRate; fire = true; } }
                if (fire) { Left = dir == 1; Right = dir == 2; Up = dir == 3; Down = dir == 4; }
            }
            else repeatDir = 0;

            bool typing = TextEntryActive;
            Select = Edge(Buttons.A) || KeyEdge(Keys.Enter) || !typing && (KeyEdge(Keys.Space) || KeyEdge(Keys.A));
            Back = Edge(Buttons.B) || KeyEdge(Keys.Escape) || !typing && (KeyEdge(Keys.Back) || KeyEdge(Keys.B)) || MouseRightClicked;
            PageLeft = Edge(Buttons.LeftShoulder) || KeyEdge(Keys.PageUp) || !typing && KeyEdge(Keys.OemComma) || Wheel > 0;
            PageRight = Edge(Buttons.RightShoulder) || KeyEdge(Keys.PageDown) || !typing && KeyEdge(Keys.OemPeriod) || Wheel < 0;
            Y = Edge(Buttons.Y) || KeyEdge(Keys.Tab) || !typing && KeyEdge(Keys.Y);
            X = Edge(Buttons.X) || !typing && (KeyEdge(Keys.X) || KeyEdge(Keys.Delete));
            Start = Edge(Buttons.Start) || KeyEdge(Keys.F5);   // F5 is the keyboard's Start (the gallery's "play")
            ReadTriggers();
            WhichLegend();

            if (Suppress)
            {
                Left = Right = Up = Down = false;
                Select = Back = PageLeft = PageRight = Y = X = Start = false;
                MouseMoved = MouseClicked = MouseRightClicked = MouseHeld = false;
                Wheel = 0;
                TriggerLeft = TriggerRight = 0;
                RightStick = Vector2.Zero; StickMoved = false;
                LeftShoulderHeld = RightShoulderHeld = RightThumbHeld = false;
            }
            if (Synthetic != null) Synthetic(this);
        }

        /// <summary>Scripted presses, applied after the real devices (--fuzz).</summary>
        public Action<Input> Synthetic;

        public void SetMouse(Vector2 designPos, bool clicked, bool rightClicked)
        {
            MouseMoved = designPos != MousePos;
            MousePos = designPos;
            MouseClicked = clicked;
            MouseRightClicked = rightClicked;
            MouseHeld = clicked;
        }

        /// <summary>This frame's click is taken (the legend made it a button press).</summary>
        public void ConsumeClick() { MouseClicked = false; }

        /// <summary>While a text field owns the keyboard, Space and Backspace are typing, not UI.</summary>
        public bool TextEntryActive { get; set; }

        /// <summary>Drop every real device: a scripted run must not be driven by a pad plugged in.</summary>
        public bool Suppress { get; set; }

        private void ReadTriggers()
        {
            TriggerLeft = TriggerRight = 0;
            bool engaged = false;
            for (PlayerIndex i = PlayerIndex.One; i <= PlayerIndex.Four && !engaged; i++)
            {
                GamePadCapabilities caps = GamePad.GetCapabilities(i);
                if (!caps.IsConnected || caps.GamePadType != GamePadType.GamePad) continue;
                GamePadState s = GamePad.GetState(i, GamePadDeadZone.None);
                Take((int)Math.Round(s.Triggers.Left * 255f), (int)Math.Round(s.Triggers.Right * 255f), ref engaged);
            }
            // the console's [ and ] (XUI_KeyboardToPadKey: LT / RT), and Q / E ([ours])
            bool q = kb.IsKeyDown(Keys.Q) || kb.IsKeyDown(Keys.OemOpenBrackets), e = kb.IsKeyDown(Keys.E) || kb.IsKeyDown(Keys.OemCloseBrackets);
            if (!engaged && (q || e)) Take(q ? 255 : 0, e ? 255 : 0, ref engaged);

            RightStick = Vector2.Zero; StickMoved = false;
            LeftShoulderHeld = RightShoulderHeld = RightThumbHeld = false;
            for (PlayerIndex i = PlayerIndex.One; i <= PlayerIndex.Four; i++)
            {
                GamePadCapabilities caps = GamePad.GetCapabilities(i);
                if (!caps.IsConnected || caps.GamePadType != GamePadType.GamePad) continue;
                GamePadState g = GamePad.GetState(i, GamePadDeadZone.None);
                if (!StickMoved)
                {
                    Vector2 s = g.ThumbSticks.Right * 32767f;
                    RightStick = new Vector2((float)Math.Round(s.X), (float)Math.Round(s.Y));
                    StickMoved = Math.Abs(RightStick.X) > 8689f || Math.Abs(RightStick.Y) > 8689f;
                }
                LeftShoulderHeld |= g.IsButtonDown(Buttons.LeftShoulder);     // wButtons 0x100
                RightShoulderHeld |= g.IsButtonDown(Buttons.RightShoulder);   // 0x200
                RightThumbHeld |= g.IsButtonDown(Buttons.RightStick);         // 0x80
            }
        }

        /// <summary>The shoulders and the right stick's click, held on any pad: the resting avatar's reactions.</summary>
        public bool LeftShoulderHeld, RightShoulderHeld, RightThumbHeld;

        /// <summary>[ours] Keys, not pad buttons, in the legend: no pad, or the keyboard or mouse used since.</summary>
        public static bool KeysShown { get; private set; }
        public static bool ForceKeys;
        private bool keysLast, padSeen;

        private void WhichLegend()
        {
            if (Suppress) { KeysShown = ForceKeys; return; }
            bool connected = false;
            for (PlayerIndex i = PlayerIndex.One; i <= PlayerIndex.Four && !connected; i++)
            {
                GamePadCapabilities caps = GamePad.GetCapabilities(i);
                connected = caps.IsConnected && caps.GamePadType == GamePadType.GamePad;
            }
            if (connected && !padSeen) keysLast = false;      // a controller just arrived: its buttons
            padSeen = connected;
            bool padUsed = pad.Buttons != padPrev.Buttons || pad.DPad != padPrev.DPad
                           || pad.ThumbSticks.Left.Length() > 0.5f || pad.ThumbSticks.Right.Length() > 0.5f
                           || pad.Triggers.Left > 0.2f || pad.Triggers.Right > 0.2f;
            bool keysUsed = kb.GetPressedKeys().Length > 0 || MouseMoved || MouseClicked || MouseRightClicked || Wheel != 0;
            if (padUsed) keysLast = false;
            else if (keysUsed) keysLast = true;
            KeysShown = ForceKeys || !connected || keysLast;
        }

        private void Take(int lt, int rt, ref bool engaged)
        {
            TriggerLeft = lt; TriggerRight = rt;
            engaged = lt > 0x20 || rt > 0x20;
        }

        public bool KeyEdge(Keys k) { return !Suppress && kb.IsKeyDown(k) && !kbPrev.IsKeyDown(k); }
        private bool KeyDown(Keys k) { return kb.IsKeyDown(k); }
        private bool Edge(Buttons b) { return pad.IsButtonDown(b) && !padPrev.IsButtonDown(b); }

        private bool PadDir(int x, int y)
        {
            const float dead = 0.5f;
            if (x < 0) return pad.DPad.Left == ButtonState.Pressed || pad.ThumbSticks.Left.X < -dead;
            if (x > 0) return pad.DPad.Right == ButtonState.Pressed || pad.ThumbSticks.Left.X > dead;
            if (y > 0) return pad.DPad.Up == ButtonState.Pressed || pad.ThumbSticks.Left.Y > dead;
            return pad.DPad.Down == ButtonState.Pressed || pad.ThumbSticks.Left.Y < -dead;
        }

        public bool MouseIn(Rectangle r) { return r.Contains((int)MousePos.X, (int)MousePos.Y); }
    }
}
