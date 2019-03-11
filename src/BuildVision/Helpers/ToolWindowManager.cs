using System;

using EnvDTE;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell.Interop;
using BuildVision.Core;
using BuildVision.Tool;
using BuildVision.UI.Models;

namespace BuildVision.Helpers
{
    public class ToolWindowManager
    {
        private readonly DTE _dte;
        private readonly IVsWindowFrame _windowFrame;
        private readonly Window _window;

        public ToolWindowManager(DTE dte, IVsWindowFrame windowFrame)
        {
            if (_dte == null)
                throw new InvalidOperationException("Unable to get DTE instance.");

            _windowFrame = windowFrame;
            if (_windowFrame == null)
                throw new InvalidOperationException("Unable to get IVsWindowFrame instance.");

            _window = GetWindowInstance(_dte, typeof(BuildVisionPane).GUID);
            if (_window == null)
                throw new InvalidOperationException("Unable to get Window instance.");
        }

        public bool IsVisible()
        {
            int isOnScreen;
            int result = _windowFrame.IsOnScreen(out isOnScreen);
            return (result == VSConstants.S_OK
                    && isOnScreen == 1
                    && _windowFrame.IsVisible() == VSConstants.S_OK);
        }

        public void Show()
        {
            _windowFrame.Show();
        }

        public void ShowNoActivate()
        {
            _windowFrame.ShowNoActivate();
        }

        public void Close()
        {
            _windowFrame.Hide();
        }

        public void Hide()
        {
            if (!IsVisible())
                return;

            Window window = _window;
            switch (window.WindowState)
            {
                case vsWindowState.vsWindowStateNormal:
                    if (!window.IsFloating)
                        MinimizeToolWindow();
                    else
                        Close();
                    break;

                case vsWindowState.vsWindowStateMaximize:
                    Close();
                    break;

                case vsWindowState.vsWindowStateMinimize:
                    break;

                default:
                    throw new InvalidOperationException();
            }
        }

        private void MinimizeToolWindow()
        {
            // Note: throws exception if _window.WindowState = vsWindowState.vsWindowStateMaximize 
            // or _window.IsFloating.
            _window.AutoHides = true;

            Window win = _dte.ActiveWindow;
            if (win != _window)
            {
                win.Activate();
                return;
            }

            Document doc = _dte.ActiveDocument;
            if (doc != null)
                doc.Activate();
        }

        private static Window GetWindowInstance(DTE dte, Guid windowGuid)
        {
            Windows windows = dte.Windows;

            for (int i = 1; i <= windows.Count; i++)
            {
                Window window = windows.Item(i);
                Guid guid;
                if (Guid.TryParse(window.ObjectKind, out guid) && guid == windowGuid)
                    return window;
            }

            return null;
        }

        private void ApplyToolWindowStateAction(WindowState windowState)
        {
            switch (windowState)
            {
                case WindowState.Nothing:
                    break;
                case WindowState.Show:
                    Show();
                    break;
                case WindowState.ShowNoActivate:
                    ShowNoActivate();
                    break;
                case WindowState.Hide:
                    Hide();
                    break;
                case WindowState.Close:
                    Close();
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(windowState));
            }
        }
    }
}
