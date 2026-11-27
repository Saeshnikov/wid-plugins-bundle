# Happ Control v0.9

Compact WidBar preview: flat clickable widget surface (open app) and one separate shield button. The Happ label has no button chrome. The two actions are sibling hit targets, not nested controls. No Flyout. Same `Personal.Happ.Widget` package and `com.personal.happ` plugin ID. Intended for Happ Desktop 4.5.4 (625) on Windows.

## Status and switching

Both shield icons inherit the same native button foreground and use full opacity in confirmed states. Original Microsoft Fluent UI Shield Checkmark and Shield Dismiss 16 Regular SVG paths are rendered in a fixed viewBox. During a connection transition, a native monochrome indeterminate WinUI ProgressRing replaces the shield in a fixed-size slot; both actions remain disabled until the operation completes. Opening Happ does not activate the ring.

The user's Windows process snapshots show that `C:\Program Files\FlyFrogLLC\Happ\core\xray.exe` exists while connected and disappears when disconnected. The widget reads process metadata every two seconds without opening any windows, making network requests or running shell scripts. It matches Xray's full executable path against the standard Happ install path and the `core\xray.exe` path under the current-session Happ executable directory. A matching core in the current session or service session 0 counts as running; unrelated readable Xray executables do not. Unreadable processes or an unknown installation produce an unknown state when no matching core is verified. This is a local core activity indicator, not proof of connectivity or actual routing. Other modes/versions are unverified.

- Monochrome shield with checkmark: core running; click to disconnect.
- Monochrome shield with cross: core stopped; click to connect.
- Animated ring: a connect/disconnect operation is in progress. No repeat clicks.
- Question mark: unknown state, switching disabled. Opening Happ disables both buttons without activating the ring. Hover over the flat widget surface for status and last result.
- Click the widget surface (outside the shield control) to open the app using `happ://open`.

Before sending a command, the controller reads the state again; if the desired state already holds or is unknown, it sends nothing. It uses `happ://connect_without_ui` or `happ://disconnect_without_ui` and preserves the v0.4 return-to-tray mechanism. After sending, it observes the requested core state for up to eight seconds following the command/close phase, with no automatic retries. The indicator follows observed state, not the command. A brief window flash can still occur; a normal close is requested after two seconds only if Happ was hidden and the window is unambiguously identified. No UI Automation, forced window hiding, or VPN configuration changes.

## Install or update

For a clean checkout, first follow the [project generation instructions](../../README.md#build-from-a-clean-checkout). Exit WidBar from its tray menu and keep Happ running. From this widget's directory:

```powershell
.\Install-Widget.ps1
```

The installer deploys to `%LOCALAPPDATA%\WidBarWidgets\Happ` regardless of where the repository lives. If you want to reuse a generated project outside this repository, pass its `Personal.Happ` directory with `-ProjectDirectory`. Restart WidBar. Existing width settings remain valid; the default is 100 DIP, range 80-240. Existing ReturnToTray settings are preserved.

## Windows acceptance checks (not run here)

1. With Happ in tray, toggle externally through its own tray menu. Shield changes shape within about two seconds, without opening Happ.
2. Click Power when stopped; confirm the monochrome ring animates without changing button width, Happ connects, returns to tray if initially hidden, and the shield with checkmark appears. Repeat to disconnect. Verify the ring also stops after an error or transition timeout.
3. Open Happ by clicking the flat widget surface (including its empty space); verify that the shield button never also opens the app; verify X closes normally. Switching while Happ is visible must leave it visible.
4. If an unrelated Xray runs, verify it is not counted. If metadata cannot be read, the shield button should become disabled rather than guessing.
5. Verify old settings, width slider, and GitHub Activity are unaffected.

Building from source does not exercise rendering, process permissions, or connection switching inside WidBar.

## Icon attribution

See [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt) for Microsoft Fluent UI System Icons (MIT), source paths and source revisions.
