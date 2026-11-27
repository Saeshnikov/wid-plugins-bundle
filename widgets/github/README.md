# GitHub Activity for WidBar — v0.8

## Install or update

For a first install, follow the [project generation instructions](../../README.md#build-from-a-clean-checkout). To install or update, exit WidBar from its tray menu, not just its settings window. From this widget's directory, run:

```powershell
.\Update-Widget.ps1
```

The updater builds Release x64 and registers the loose package in `%LOCALAPPDATA%\WidBarWidgets\GitHub`. `-ProjectDirectory` optionally selects a generated `Personal.GitHub` build project; it does not change the installation directory. Restart WidBar after success.

If Windows still points to a legacy registration outside `%LOCALAPPDATA%\WidBarWidgets\GitHub`, the updater stops before changing any files. Migrate that development registration with application data preserved; keep the old directory until the widget has been verified in WidBar.

## Preview width (v0.8)

The new **Preview width** slider in widget settings sets the requested taskbar slot width from 100 to 240 logical pixels (step 2, default 156). The setting is stored per widget instance in `PreviewWidth`; old settings without the new key retain the v0.7 width. The SDK's `OnSettingsDraftChanged` callback applies the draft, and `Context.RequestPreviewRefresh()` asks WidBar to update the slot. Cancel restores the original JSON; Save persists the choice. Actual slot allocation depends on WidBar and available taskbar space. The counters remain centered; a uniform down-only Viewbox allows them to shrink when needed, not grow at larger widths. The flyout is unchanged.

A width-only change does not clear cached PRs, restart polling or invalidate an in-flight API request. No API, icon, repository-selection or updater changes. See https://github.com/andelby/widbar-widget-template/blob/main/wiki/Preview-Flyout-Settings.md for the documented dynamic-width contract. Verify live width, Save, Cancel, two instances, and narrow widths with all counters visible. Windows runtime behavior is not verified here.

## Repository selection

1. Open widget settings and keep Demo mode off.
2. Click **Load my repositories** to fetch repositories accessible through the locally authenticated `gh.exe` account (owned, collaborator and organization-member repositories). Use the search box and check the repositories to track. The list displays up to 100 matches at once; refine the search for more.
3. The **Tracked repositories (owner/name)** field also allows manually entering repositories separated by commas or newlines. Useful for public repositories not listed by `user/repos`. Selection is case-insensitively deduplicated and saved with the widget's existing settings. Clear the list or click **Track all** to return to the previous all-repositories behavior (organization filter still applies if set). An explicit nonempty selection overrides the organization filter.
4. Click **Test GitHub connection** or Save and refresh. All three counters and lists use the same repository filter on the server. A nonempty list is not just a filter over the first 20 results. Searches use the existing legacy REST search semantics (multiple repo qualifiers are OR-ed), in groups of eight repositories. Results are merged by update time and the newest 20 displayed per category. A failed chunk invalidates its category's total rather than silently counting it as zero. Many selected repositories or multiple instances increase API usage; keep polling at 120 seconds or slower if needed.

Manual verification (optional):

```powershell
gh api --hostname github.com --method GET user/repos --paginate --slurp -f per_page=100 -f 'affiliation=owner,collaborator,organization_member'
gh api --hostname github.com --method GET search/issues -f 'q=is:pr is:open author:@me repo:OWNER/REPO' --jq .total_count
```

The repository loader is explicit: demo polling remains offline, but clicking Load my repositories or Test GitHub connection intentionally contacts GitHub. No token storage or additional permissions. The definitions of My PRs, To review and Failed are unchanged (Failed = failed commit status, not all Actions checks).

## Icon changes

All six original upstream SVG path strings remain byte-for-byte unchanged. Instead of assigning a small size directly to PathIcon, use a 16x16 Canvas and Path with the original nonzero fill rule, then a uniform Viewbox. This preserves SVG viewBox coordinates at different sizes. Use 16 logical pixels for taskbar and row icons including external-link arrows, fixed columns for row alignment, and native theme brushes. No new custom-drawn icons. See THIRD-PARTY-NOTICES.txt.

## Verification and limits

Building from source does not exercise rendering, real repository loading or filtered API results. Check one repository, two repositories, an empty filter, Cancel, both themes and DPI scales. The `Repositories` field defaults to empty for existing settings.
