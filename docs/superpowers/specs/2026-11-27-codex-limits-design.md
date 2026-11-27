# Codex Limits for WidBar

Show how much of the Codex allowance is left and how far away its reset is, without opening Codex.

- One indicator per available window of the main Codex quota, shorter first. The centered number is the remaining percentage; the `%` sign is shown only in the tooltip. A ring drains in proportion to the time remaining in that window. No textual countdown.
- Refresh usage every minute; redraw the ring locally between requests. Show the percentage without a ring if the reset cannot be calculated. Show `—` when usage is unavailable, rather than `0%`. At reset, fetch again instead of assuming the allowance is restored.
- Ship as a separate Windows WidBar widget using the existing C#/WinUI template. Do not change the Happ or GitHub widgets, make inference requests, or monitor ordinary ChatGPT messages.

## Approaches

Use Codex App Server's documented `account/rateLimits/read` over local stdio. It supplies usage, window duration, and reset time, and lets Codex manage its own sign-in. Scraping the dashboard would depend on browser state and page layout; parsing session logs would return historical snapshots. Neither is needed, and the widget will not read cookies or token files.
