# KeyClick Fun Stats and Activity Visualizations

## Summary

Add an offline, privacy-safe Fun Stats system, customizable milestone dashboard, social sharing, and richer activity charts. Reorder Home to: Sounds → visual keyboard → Fun Stats → sound pack/volume → audio output. Preserve the current uncommitted heatmap and key-breakdown work.

## Key Changes

### Fun Stats experience

- Add one compact fun comparison to all eight overview cards. Keep it stable across one-second refreshes and rotate using a saved cadence: 10 minutes, 1 hour default, daily, app launch, card click, or manual only.
- Make metric cards keyboard-accessible buttons opening a scrollable detail dialog with all enabled comparisons, formulas, approximation labels, and offline source/year notes.
- Add a reusable Fun Stats dashboard:
  - Six default tiles, user-reorderable with up to twelve.
  - Statistics placement: after metric cards and before activity charts, following the page period.
  - Home placement: after the visual keyboard, with its own selector defaulting to All time.
  - Linear milestone bars, distance routes, radial progress, and equivalence tiles, selected according to metric type.
- Built-in milestones advance to the next target after completion while showing achieved multiples. Custom milestones remain completed at 100%.
- Include at least 36 English/French comparisons covering:
  - Dated populations, planets, food quantities, an amoeba as one cell, the 959 somatic cells of adult hermaphrodite *C. elegans*, and approximate insect-colony sizes.
  - Statue of Liberty, Eiffel Tower, Burj Khalifa, Everest, Earth circumference, mean Earth–Moon distance, and clearly qualified planetary-distance estimates.
  - Estimated words, pages, and books from typing presses; active time as songs, episodes, films, and days; CPS as tempo/BPM; typing-rate comparisons; busiest-hour personas and activity share.
- Bundle the immutable, versioned catalog and schema under `shared/`; display `≈` for estimates and keep facts fully offline with no runtime fetching.

### Customization and persistence

- Add one Manage Fun Stats dialog for:
  - Master enable/disable, metric-card facts, category/fact toggles, tile selection, and Up/Down reordering.
  - Structured custom milestones using a whitelisted metric, positive target, automatic unit, and 1–80-character user label—no formulas or executable content.
  - Rotation cadence, copy mode, Home period, chart preferences, and scroll-distance estimate.
- Estimate total scrolling as `(vertical + horizontal detents) × centimeters-per-detent`, defaulting to 1.27 cm and always labeling it estimated.
- Support direct entry/reset plus optional calibration: enter a known traveled distance, scroll that distance in a local test surface, and calculate centimeters per detent. Validate values between 0.01 and 100 cm per detent.
- Extend `AppSettings` with stable fact IDs, selected order, custom definitions, cadence, copy mode, scroll calibration, and chart choices. Sanitize imported collections, enforce the twelve-tile limit, and transfer these preferences through profiles.
- Use settings JSON only; no SQLite migration or production SQL is required.

### Sharing

- Add a Copy button to each Fun Stats section with a localized tooltip shown after 1,000 ms: “Take a screenshot of your current Fun Stats and copy it to share on social media or with friends.”
- Persist three copy modes:
  - Image only by default.
  - Image plus a localized plain-text caption in one clipboard payload.
  - Current visible app view.
- Render a clean branded share image locally: 1200×630 for up to six tiles and 1200×1200 for seven to twelve. Include the selected period and generation date, but never application names, paths, typed content, or other private data.
- Show localized success/failure feedback. Nothing is uploaded or transmitted.

### Activity visualizations

- Replace the fixed activity line chart with a reusable chart supporting:
  - Metric families: Counts, Rates, and Active time.
  - Line and grouped-bar views for all families; donut view only for meaningful aggregate Counts and Active-time distributions.
  - Auto, hourly, daily, weekly, and monthly grouping, with Auto choosing a readable bucket size and unavailable choices disabled when they would exceed 500 points.
  - Series toggles for keyboard, pointer, vertical/horizontal scrolling, WPM/CPS averages and peaks, and total/keyboard/pointer active minutes.
- Put metric family, view, and grouping controls at the far right of the title row, wrapping below it at minimum window width. Place the longer series checklist behind one Customize control.
- When comparison mode is active, show aligned muted/dashed comparison data for line/bar charts and comparison deltas in donut tooltips.
- Add a clamped floating tooltip that follows the pointer, highlights the nearest point/bar/segment, and shows the bucket range and enabled values. Also support keyboard point navigation and accessible summaries.
- Animate only initial progress/chart presentation, never each refresh, and disable animation when Reduced Motion is enabled. Add no shadows or translate-on-hover effects.

## Interfaces and Architecture

- Extend `StatisticsTrendPoint` with typing presses, keyboard/pointer active milliseconds, and peak typing/clicking values so WPM/CPS and active-time trends are calculated correctly per bucket.
- Add presentation enums/models for metric family, view type, granularity, enabled series, Fun Stat definitions, progress state, rotation cadence, and clipboard mode.
- Re-aggregate existing hourly trend data in the presentation layer; do not add database queries per tile or touch the Raw Input callback.
- Refactor the current custom WPF renderer into a generic statistics chart and isolate clipboard/rendering behind a testable service.
- Add all new UI strings and fact text to both English and French resources.
- Treat this significant backward-compatible feature as version `1.5.0`, superseding the unpublished working-tree `1.4.5` bump.

## Test Plan

- Unit-test catalog validation, formulas, SI/unit formatting, milestone advancement, zero/huge values, deterministic rotation buckets, custom validation, and scroll calibration.
- Test hourly-to-monthly aggregation, WPM/CPS calculations, active-time series, Auto granularity, series compatibility, comparisons, and the 500-point limit.
- Test settings/profile round trips, older settings defaults, malformed imported definitions, stable IDs, twelve-tile enforcement, and EN/FR parity.
- Test all copy modes, bitmap dimensions, caption contents, clipboard failure handling, and absence of private application data.
- Test card/detail accessibility, chart pointer and keyboard hit-testing, tooltip clamping, responsive headers, minimum window size, high DPI, Reduced Motion, and Light/Dark/System themes.
- Run `dotnet build KeyClick.sln -c Release`, `dotnet test KeyClick.sln -c Release`, and the privacy-boundary check. Baseline currently passes all 84 tests.
- Do not package, commit, push, publish, or deploy unless separately requested.

## Assumptions and Deferred Ideas

- Built-in facts are curated, dated, and approximate where necessary; KeyClick will not fetch live population or astronomical data.
- User-created labels remain exactly as entered rather than being automatically translated.
- Additional included ideas are typing-to-book equivalents, click tempo, time-of-day personas, most-used key/group insights, and privacy-safe local milestone celebrations.
- Persistent badge history, animated confetti themes, and downloadable share templates are deferred; they would add storage and release scope beyond this feature.
