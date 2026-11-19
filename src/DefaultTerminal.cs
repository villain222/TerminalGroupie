using System;
using Microsoft.Win32;

namespace TerminalGroupie
{
    /// <summary>
    /// Manages the Windows default-terminal delegation registry keys so that newly
    /// launched consoles go to Windows Console Host (enabling our wrapper to work).
    ///
    /// Registry: HKCU\Console\%%Startup  — two values:
    ///   DelegationConsole       = CLSID string for console-hosting terminal
    ///   DelegationTerminal      = CLSID string for full-window terminal
    ///
    /// The Windows Console Host CLSID (same as the Settings UI writes):
    ///     {B23D10C0-E52E-411E-9D5B-C09FDF709C7D}
    ///
    /// Windows Terminal CLSID:
    ///     {2EACA947-7F5F-4CFA-BA87-8F7FBEEFBE69}
    /// </summary>
    public static class DefaultTerminal
    {
        private const string StartupKeyPath = @"Console\%%Startup";

        // Windows Console Host CLSID — the delegate we want to set.
        public const string WchCLSID = "{B23D10C0-E52E-411E-9D5B-C09FDF709C7D}";

        // Windows Terminal CLSID — what Settings UI writes on Win11 when user picks WT.
        public const string WtCLSID = "{2EACA947-7F5F-4CFA-BA87-8F7FBEEFBE69}";

        private static readonly string _keyPath = "HKEY_CURRENT_USER\\" + StartupKeyPath; // for reference/debugging

        /// <summary>Current delegation pair (Console, Terminal), or null if the key doesn't exist.</summary>
        public static Tuple<string, string>? GetCurrent()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(StartupKeyPath);
                if (key == null) return null;

                object? consoleVal = key.GetValue("DelegationConsole");
                object? terminalVal = key.GetValue("DelegationTerminal");

                string? c = consoleVal?.ToString();
                string? t = terminalVal?.ToString();

                // Both values must be present to form a valid pair.
                if (string.IsNullOrEmpty(c) || string.IsNullOrEmpty(t)) return null;
                return Tuple.Create(c, t);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>True when the current delegate is Windows Terminal.</summary>
        public static bool IsWindowsTerminal()
        {
            var pair = GetCurrent();
            if (pair == null) return false; // no override → let Windows decide (on Win11 this defaults to WT anyway).

            string c = pair.Item1.Trim().Trim('{', '}').Replace(" ", "");
            string t = pair.Item2.Trim().Trim('{', '}').Replace(" ", "").ToUpperInvariant();
            string expectedWt = WtCLSID.ToUpperInvariant();

            return t == expectedWt; // Terminal delegate is WT — Console may differ but it doesn't matter.
        }

        /// <summary>True when the current delegate is already Windows Console Host.</summary>
        public static bool IsWindowsConsoleHost()
        {
            var pair = GetCurrent();
            if (pair == null) return false;

            string t = pair.Item2.Trim().Trim('{', '}').Replace(" ", "").ToUpperInvariant();
            string expectedWch = WchCLSID.ToUpperInvariant();
            return t == expectedWch;
        }

        /// <summary>True when a Group Policy forces the default terminal (overrides HKCU).</summary>
        public static bool DefaultTerminalLockedByPolicy()
        {
            try
            {
                using var policyKey = Registry.LocalMachine.OpenSubKey(
                    @"SOFTWARE\Policies\Microsoft\Windows Terminal");
                if (policyKey == null) return false;

                object? val = policyKey.GetValue("DefaultTerminalApplication");
                return val != null && !string.IsNullOrEmpty(val.ToString());
            }
            catch
            {
                // Best effort: a crash means "no policy" — we won't break on this.
                return false;
            }
        }

        /// <summary>Save the originals and write both delegation values to Windows Console Host.</summary>
        public static bool SaveAndSetToWindowsConsoleHost()
        {
            var current = GetCurrent();
            if (current == null)
            {
                // No existing pair — just set ours. Nothing to restore later, but that's fine.
                return WritePair(WchCLSID, WchCLSID);
            }

            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(StartupKeyPath);
                if (key == null) return false;

                // Save originals as backup values so we can restore them.
                key.SetValue("DelegationConsole_Original", current.Item1, RegistryValueKind.String);
                key.SetValue("DelegationTerminal_Original", current.Item2, RegistryValueKind.String);

                return WritePair(WchCLSID, WchCLSID);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Restore the original delegation values saved by SaveAndSetToWindowsConsoleHost.</summary>
        public static bool RestoreOriginal()
        {
            string? origC, origT;
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(StartupKeyPath);
                if (key == null) return false;

                object? cVal = key.GetValue("DelegationConsole_Original");
                object? tVal = key.GetValue("DelegationTerminal_Original");
                origC = cVal?.ToString();
                origT = tVal?.ToString();
            }
            catch
            {
                return false;
            }

            if (string.IsNullOrEmpty(origC) || string.IsNullOrEmpty(origT))
            {
                // No saved originals — nothing to restore. Clean up backup keys and try a neutral pair.
                try
                {
                    using var key = Registry.CurrentUser.CreateSubKey(StartupKeyPath);
                    if (key != null)
                    {
                        key.DeleteValue("DelegationConsole_Original", false);
                        key.DeleteValue("DelegationTerminal_Original", false);
                        // Set to "let Windows decide" — empty CLSID.
                        key.SetValue("DelegationConsole", "{00000000-0000-0000-000000000000}", RegistryValueKind.String);
                        key.SetValue("DelegationTerminal", "{00000000-0000-0000-000000000000}", RegistryValueKind.String);
                    }
                }
                catch { /* best effort */ }

                return false; // no originals to restore.
            }

            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(StartupKeyPath);
                if (key == null) return false;

                key.SetValue("DelegationConsole", origC, RegistryValueKind.String);
                key.SetValue("DelegationTerminal", origT, RegistryValueKind.String);

                // Clean up backup values.
                key.DeleteValue("DelegationConsole_Original", false);
                key.DeleteValue("DelegationTerminal_Original", false);

                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Write the delegation pair to HKCU\Console\%%Startup.</summary>
        private static bool WritePair(string consoleCLSID, string terminalCLSID)
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(StartupKeyPath);
                if (key == null) return false;

                key.SetValue("DelegationConsole", consoleCLSID, RegistryValueKind.String);
                key.SetValue("DelegationTerminal", terminalCLSID, RegistryValueKind.String);

                // Verify the write took.
                string? readC = key.GetValue("DelegationConsole")?.ToString();
                string? readT = key.GetValue("DelegationTerminal")?.ToString();
                return readC == consoleCLSID && readT == terminalCLSID;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>True when the user has already consented (tracked in HKCU).</summary>
        public static bool HasUserConsented()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(StartupKeyPath);
                if (key == null) return false;

                object? val = key.GetValue("TerminalGroupie_Consented");
                return val != null && Convert.ToInt32(val) == 1;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Mark that the user has consented to our registry change.</summary>
        public static void MarkConsented()
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(StartupKeyPath);
                if (key == null) return;

                key.SetValue("TerminalGroupie_Consented", 1, RegistryValueKind.DWord);
            }
            catch { /* best effort */ }
        }
    }
}
