using System;
using System.Reflection;
using Foundation;
using UIKit;
using SDViOS.Diagnostics;
using SDViOS.Loader;

namespace SDViOS.Input
{
    public static class VirtualKeyboardManager
    {
        private static bool _reflectionInitialized = false;
        private static object? _keyboardDispatcherInstance = null;
        private static PropertyInfo? _subscriberProp = null;
        private static PropertyInfo? _selectedProp = null;
        private static PropertyInfo? _textProp = null;
        private static PropertyInfo? _titleProp = null;
        private static MethodInfo? _recieveTextInputMethod = null;
        private static FieldInfo? _onEnterPressedField = null;

        private static bool _isKeyboardActive = false;
        private static object? _activeSubscriber = null;

        public static void Update()
        {
            if (_isKeyboardActive) return;

            try
            {
                EnsureReflection();
                if (_keyboardDispatcherInstance == null || _subscriberProp == null) return;

                object? subscriber = _subscriberProp.GetValue(_keyboardDispatcherInstance);
                if (subscriber == null) return;

                if (_selectedProp == null)
                {
                    _selectedProp = subscriber.GetType().GetProperty("Selected");
                }

                bool isSelected = false;
                if (_selectedProp != null)
                {
                    isSelected = (bool)(_selectedProp.GetValue(subscriber) ?? false);
                }

                if (isSelected && !_isKeyboardActive)
                {
                    ShowKeyboard(subscriber);
                }
            }
            catch (Exception ex)
            {
                EngineLogger.LogWarning($"[VirtualKeyboardManager] Update error: {ex.Message}");
            }
        }

        public static void ShowKeyboard(object subscriber)
        {
            if (_isKeyboardActive) return;
            _isKeyboardActive = true;
            _activeSubscriber = subscriber;

            UIApplication.SharedApplication.BeginInvokeOnMainThread(() =>
            {
                try
                {
                    string currentText = "";
                    string title = "Enter Text";
                    try
                    {
                        if (_textProp == null)
                        {
                            _textProp = subscriber.GetType().GetProperty("Text");
                        }
                        currentText = _textProp?.GetValue(subscriber) as string ?? "";

                        if (_titleProp == null)
                        {
                            _titleProp = subscriber.GetType().GetProperty("TitleText");
                        }
                        var titleVal = _titleProp?.GetValue(subscriber) as string;
                        if (!string.IsNullOrEmpty(titleVal))
                        {
                            title = titleVal;
                        }
                    }
                    catch { }

                    var rootVC = GameHost.GetActiveRootViewController();
                    if (rootVC == null)
                    {
                        _isKeyboardActive = false;
                        _activeSubscriber = null;
                        return;
                    }

                    var alert = UIAlertController.Create(title, null, UIAlertControllerStyle.Alert);
                    UITextField? textField = null;
                    alert.AddTextField(tf =>
                    {
                        textField = tf;
                        tf.Text = currentText;
                        tf.AutocapitalizationType = UITextAutocapitalizationType.Words;
                        tf.AutocorrectionType = UITextAutocorrectionType.Default;
                        tf.ReturnKeyType = UIReturnKeyType.Done;
                        tf.ShouldReturn = _ =>
                        {
                            alert.DismissViewController(true, () =>
                            {
                                SubmitText(subscriber, textField?.Text ?? "");
                            });
                            return true;
                        };
                    });

                    alert.AddAction(UIAlertAction.Create("OK", UIAlertActionStyle.Default, _ =>
                    {
                        SubmitText(subscriber, textField?.Text ?? "");
                    }));

                    alert.AddAction(UIAlertAction.Create("Cancel", UIAlertActionStyle.Cancel, _ =>
                    {
                        CancelInput(subscriber);
                    }));

                    rootVC.PresentViewController(alert, true, () =>
                    {
                        textField?.BecomeFirstResponder();
                    });
                }
                catch (Exception ex)
                {
                    EngineLogger.LogWarning($"[VirtualKeyboardManager] Error showing keyboard: {ex.Message}");
                    _isKeyboardActive = false;
                    _activeSubscriber = null;
                }
            });
        }

        public static void PromptManualInput(Action<string> onCompleted)
        {
            if (_isKeyboardActive) return;
            _isKeyboardActive = true;

            UIApplication.SharedApplication.BeginInvokeOnMainThread(() =>
            {
                try
                {
                    var rootVC = GameHost.GetActiveRootViewController();
                    if (rootVC == null)
                    {
                        _isKeyboardActive = false;
                        return;
                    }

                    var alert = UIAlertController.Create("Keyboard Input", null, UIAlertControllerStyle.Alert);
                    UITextField? textField = null;
                    alert.AddTextField(tf =>
                    {
                        textField = tf;
                        tf.ReturnKeyType = UIReturnKeyType.Done;
                        tf.ShouldReturn = _ =>
                        {
                            alert.DismissViewController(true, () =>
                            {
                                _isKeyboardActive = false;
                                onCompleted?.Invoke(textField?.Text ?? "");
                            });
                            return true;
                        };
                    });

                    alert.AddAction(UIAlertAction.Create("OK", UIAlertActionStyle.Default, _ =>
                    {
                        _isKeyboardActive = false;
                        onCompleted?.Invoke(textField?.Text ?? "");
                    }));

                    alert.AddAction(UIAlertAction.Create("Cancel", UIAlertActionStyle.Cancel, _ =>
                    {
                        _isKeyboardActive = false;
                    }));

                    rootVC.PresentViewController(alert, true, () =>
                    {
                        textField?.BecomeFirstResponder();
                    });
                }
                catch (Exception ex)
                {
                    EngineLogger.LogWarning($"[VirtualKeyboardManager] Error in manual prompt: {ex.Message}");
                    _isKeyboardActive = false;
                }
            });
        }

        private static void SubmitText(object subscriber, string newText)
        {
            try
            {
                EngineLogger.Log($"[VirtualKeyboardManager] Submitting text: '{newText}'");

                // 1. Direct property assignment
                if (_textProp != null)
                {
                    _textProp.SetValue(subscriber, newText);
                }

                // 2. Call RecieveTextInput(string)
                if (_recieveTextInputMethod == null)
                {
                    _recieveTextInputMethod = subscriber.GetType().GetMethod("RecieveTextInput", new[] { typeof(string) });
                }
                _recieveTextInputMethod?.Invoke(subscriber, new object[] { newText });

                // 3. Deselect
                if (_selectedProp != null)
                {
                    _selectedProp.SetValue(subscriber, false);
                }

                // 4. Trigger Enter event
                if (_onEnterPressedField == null)
                {
                    _onEnterPressedField = subscriber.GetType().GetField("OnEnterPressed", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                }
                var del = _onEnterPressedField?.GetValue(subscriber) as MulticastDelegate;
                del?.DynamicInvoke(subscriber);
            }
            catch (Exception ex)
            {
                EngineLogger.LogWarning($"[VirtualKeyboardManager] SubmitText warning: {ex.Message}");
            }
            finally
            {
                _isKeyboardActive = false;
                _activeSubscriber = null;
            }
        }

        private static void CancelInput(object subscriber)
        {
            try
            {
                if (_selectedProp != null)
                {
                    _selectedProp.SetValue(subscriber, false);
                }
            }
            catch { }
            finally
            {
                _isKeyboardActive = false;
                _activeSubscriber = null;
            }
        }

        private static DateTime _lastReflectionAttempt = DateTime.MinValue;
        private static bool _loggedReflectionWarning = false;

        private static void EnsureReflection()
        {
            if (_reflectionInitialized && _keyboardDispatcherInstance != null) return;
            if ((DateTime.UtcNow - _lastReflectionAttempt).TotalSeconds < 2.0) return;
            _lastReflectionAttempt = DateTime.UtcNow;

            try
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    var g1Type = asm.GetType("StardewValley.Game1");
                    if (g1Type != null)
                    {
                        var dispatcherField = g1Type.GetField("instanceKeyboardDispatcher", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                                           ?? g1Type.GetField("keyboardDispatcher", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                        var dispatcherProp = g1Type.GetProperty("keyboardDispatcher", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

                        object? dispatcher = null;
                        try
                        {
                            dispatcher = dispatcherField?.GetValue(null) ?? dispatcherProp?.GetValue(null);
                        }
                        catch { }

                        if (dispatcher != null)
                        {
                            _keyboardDispatcherInstance = dispatcher;
                            var kdType = dispatcher.GetType();
                            _subscriberProp = kdType.GetProperty("Subscriber", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                            _reflectionInitialized = true;
                            EngineLogger.Log("[VirtualKeyboardManager] Hooked Stardew Valley keyboardDispatcher successfully.");
                            break;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                if (!_loggedReflectionWarning)
                {
                    _loggedReflectionWarning = true;
                    EngineLogger.LogWarning($"[VirtualKeyboardManager] Reflection hook warning: {ex.Message}");
                }
            }
        }
    }
}
