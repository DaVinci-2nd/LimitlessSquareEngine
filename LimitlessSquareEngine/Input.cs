using Silk.NET.Windowing;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading.Tasks;
using Silk.NET.Input;

namespace LimitlessSquareEngine
{
    internal class Input
    {
        private readonly IInputContext _inputContext;

        private readonly HashSet<Key> _currentKeys = new HashSet<Key>();
        private readonly HashSet<Key> _previousKeys = new HashSet<Key>();

        private readonly HashSet<MouseButton> _currentMouseButtons = new HashSet<MouseButton>();
        private readonly HashSet<MouseButton> _previousMouseButtons = new HashSet<MouseButton>();

        private readonly Dictionary<int, HashSet<ButtonName>> _currentGamepadButtons = new Dictionary<int, HashSet<ButtonName>>();
        private readonly Dictionary<int, HashSet<ButtonName>> _previousGamepadButtons = new Dictionary<int, HashSet<ButtonName>>();

        private Vector2 _mousePosition = Vector2.Zero;
        private Vector2 _previousMousePosition = Vector2.Zero;
        private Vector2 _mouseDelta = Vector2.Zero;
        private Vector2 _wheelDelta = Vector2.Zero;
        private Vector2 _pendingWheelDelta = Vector2.Zero;
        private bool _mouseInitialized = false;

        public Input(IWindow window)
        {
            _inputContext = window.CreateInput();

            foreach (var mouse in _inputContext.Mice)
            {
                mouse.Scroll += OnMouseScroll;
            }
        }

        public void Update()
        {
            CopySet(_currentKeys, _previousKeys);
            CopySet(_currentMouseButtons, _previousMouseButtons);
            CopyGamepadButtons(_currentGamepadButtons, _previousGamepadButtons);

            _currentKeys.Clear();
            _currentMouseButtons.Clear();
            _currentGamepadButtons.Clear();

            UpdateKeys();
            UpdateMouseButtons();
            UpdateGamepadButtons();
            UpdateMousePosition();

            _wheelDelta = _pendingWheelDelta;
            _pendingWheelDelta = Vector2.Zero;
        }

        public bool KeyPressed(string keyName)
        {
            return TryParseKey(keyName, out var key) && _currentKeys.Contains(key);
        }

        public bool KeyDown(string keyName)
        {
            return TryParseKey(keyName, out var key) && _currentKeys.Contains(key) && !_previousKeys.Contains(key);
        }

        public bool KeyUp(string keyName)
        {
            return TryParseKey(keyName, out var key) && !_currentKeys.Contains(key) && _previousKeys.Contains(key);
        }

        public bool MouseButtonPressed(string buttonName)
        {
            return TryParseMouseButton(buttonName, out var button) && _currentMouseButtons.Contains(button);
        }

        public bool MouseButtonDown(string buttonName)
        {
            return TryParseMouseButton(buttonName, out var button) && _currentMouseButtons.Contains(button) && !_previousMouseButtons.Contains(button);
        }

        public bool MouseButtonUp(string buttonName)
        {
            return TryParseMouseButton(buttonName, out var button) && !_currentMouseButtons.Contains(button) && _previousMouseButtons.Contains(button);
        }

        public bool HasGamepad(int gamepadIndex)
        {
            return GetGamepad(gamepadIndex) != null;
        }

        public int GetGamepadCount()
        {
            int count = 0;
            foreach (var gamepad in _inputContext.Gamepads)
            {
                if (gamepad != null && gamepad.IsConnected)
                    count++;
            }
            return count;
        }

        public string GetGamepadName(int gamepadIndex)
        {
            var gamepad = GetGamepad(gamepadIndex);
            return gamepad?.Name ?? string.Empty;
        }

        public bool GamepadButtonPressed(int gamepadIndex, string buttonName)
        {
            if (!TryParseGamepadButton(buttonName, out var button))
                return false;

            return TryGetPressedGamepadButtons(_currentGamepadButtons, gamepadIndex, out var buttons) && buttons.Contains(button);
        }

        public bool GamepadButtonDown(int gamepadIndex, string buttonName)
        {
            if (!TryParseGamepadButton(buttonName, out var button))
                return false;

            bool current = TryGetPressedGamepadButtons(_currentGamepadButtons, gamepadIndex, out var currentButtons) && currentButtons.Contains(button);
            bool previous = TryGetPressedGamepadButtons(_previousGamepadButtons, gamepadIndex, out var previousButtons) && previousButtons.Contains(button);
            return current && !previous;
        }

        public bool GamepadButtonUp(int gamepadIndex, string buttonName)
        {
            if (!TryParseGamepadButton(buttonName, out var button))
                return false;

            bool current = TryGetPressedGamepadButtons(_currentGamepadButtons, gamepadIndex, out var currentButtons) && currentButtons.Contains(button);
            bool previous = TryGetPressedGamepadButtons(_previousGamepadButtons, gamepadIndex, out var previousButtons) && previousButtons.Contains(button);
            return !current && previous;
        }

        public float GetGamepadStickX(int gamepadIndex, int stickIndex)
        {
            var gamepad = GetGamepad(gamepadIndex);
            if (gamepad == null)
                return 0f;

            if (stickIndex < 0 || stickIndex >= gamepad.Thumbsticks.Count)
                return 0f;

            return gamepad.Thumbsticks[stickIndex].X;
        }

        public float GetGamepadStickY(int gamepadIndex, int stickIndex)
        {
            var gamepad = GetGamepad(gamepadIndex);
            if (gamepad == null)
                return 0f;

            if (stickIndex < 0 || stickIndex >= gamepad.Thumbsticks.Count)
                return 0f;

            return gamepad.Thumbsticks[stickIndex].Y;
        }

        public float GetGamepadTrigger(int gamepadIndex, int triggerIndex)
        {
            var gamepad = GetGamepad(gamepadIndex);
            if (gamepad == null)
                return 0f;

            if (triggerIndex < 0 || triggerIndex >= gamepad.Triggers.Count)
                return 0f;

            return gamepad.Triggers[triggerIndex].Position;
        }

        public float GetMouseX()
        {
            return _mousePosition.X;
        }

        public float GetMouseY()
        {
            return _mousePosition.Y;
        }

        public float GetMouseDeltaX()
        {
            return _mouseDelta.X;
        }

        public float GetMouseDeltaY()
        {
            return _mouseDelta.Y;
        }

        public float GetMouseWheelX()
        {
            return _wheelDelta.X;
        }

        public float GetMouseWheelY()
        {
            return _wheelDelta.Y;
        }

        private void UpdateKeys()
        {
            foreach (var keyboard in _inputContext.Keyboards)
            {
                if (keyboard == null || !keyboard.IsConnected)
                    continue;

                foreach (var key in keyboard.SupportedKeys)
                {
                    if (keyboard.IsKeyPressed(key))
                        _currentKeys.Add(key);
                }
            }
        }

        private void UpdateMouseButtons()
        {
            foreach (var mouse in _inputContext.Mice)
            {
                if (mouse == null || !mouse.IsConnected)
                    continue;

                foreach (var button in mouse.SupportedButtons)
                {
                    if (mouse.IsButtonPressed(button))
                        _currentMouseButtons.Add(button);
                }
            }
        }

        private void UpdateGamepadButtons()
        {
            for (int i = 0; i < _inputContext.Gamepads.Count; i++)
            {
                var gamepad = _inputContext.Gamepads[i];
                if (gamepad == null || !gamepad.IsConnected)
                    continue;

                var pressed = new HashSet<ButtonName>();
                foreach (var button in gamepad.Buttons)
                {
                    if (button.Pressed)
                        pressed.Add(button.Name);
                }

                _currentGamepadButtons[i] = pressed;
            }
        }

        private void UpdateMousePosition()
        {
            IMouse? mouse = null;

            foreach (var item in _inputContext.Mice)
            {
                if (item != null && item.IsConnected)
                {
                    mouse = item;
                    break;
                }
            }

            if (mouse == null)
            {
                _mouseDelta = Vector2.Zero;
                return;
            }

            var position = new Vector2(mouse.Position.X, mouse.Position.Y);

            if (!_mouseInitialized)
            {
                _mouseInitialized = true;
                _mousePosition = position;
                _previousMousePosition = position;
                _mouseDelta = Vector2.Zero;
                return;
            }

            _previousMousePosition = _mousePosition;
            _mousePosition = position;
            _mouseDelta = _mousePosition - _previousMousePosition;
        }

        private void OnMouseScroll(IMouse mouse, ScrollWheel wheel)
        {
            _pendingWheelDelta += new Vector2(wheel.X, wheel.Y);
        }

        private static void CopySet<T>(HashSet<T> source, HashSet<T> target) where T : notnull
        {
            target.Clear();
            foreach (var item in source)
                target.Add(item);
        }

        private static void CopyGamepadButtons(Dictionary<int, HashSet<ButtonName>> source, Dictionary<int, HashSet<ButtonName>> target)
        {
            target.Clear();
            foreach (var pair in source)
                target[pair.Key] = new HashSet<ButtonName>(pair.Value);
        }

        private static bool TryGetPressedGamepadButtons(Dictionary<int, HashSet<ButtonName>> source, int gamepadIndex, out HashSet<ButtonName> buttons)
        {
            return source.TryGetValue(gamepadIndex, out buttons!);
        }

        private IGamepad? GetGamepad(int gamepadIndex)
        {
            if (gamepadIndex < 0 || gamepadIndex >= _inputContext.Gamepads.Count)
                return null;

            var gamepad = _inputContext.Gamepads[gamepadIndex];
            if (gamepad == null || !gamepad.IsConnected)
                return null;

            return gamepad;
        }

        private static bool TryParseKey(string keyName, out Key key)
        {
            return Enum.TryParse(keyName, true, out key);
        }

        private static bool TryParseMouseButton(string buttonName, out MouseButton button)
        {
            return Enum.TryParse(buttonName, true, out button);
        }

        private static bool TryParseGamepadButton(string buttonName, out ButtonName button)
        {
            return Enum.TryParse(buttonName, true, out button);
        }

        public void Dispose()
        {
            foreach (var mouse in _inputContext.Mice)
            {
                mouse.Scroll -= OnMouseScroll;
            }

            _inputContext.Dispose();
        }
    }
}
