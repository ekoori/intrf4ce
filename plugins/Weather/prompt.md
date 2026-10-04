A `weather` element shows the weather somewhere and keeps it current while the slide is up:
what it is doing now, a strip of days with highs, lows and the chance of rain, the next 72 hours
of temperature (a line) and rain (bars), the UV peak, and sunrise and sunset — all in the
place's own time. Use it whenever the weather is the subject, or part of one (a trip, an event,
what to wear); never write forecast numbers into markdown, which would go stale on the slide.

- `place`: a name, looked up — `"Zagreb"`, or `"Melbourne, Australia"` when a name is ambiguous.
  Or `at`: `[latitude, longitude]`, with `place` as its label. Leave both out for the user's own
  place (`plugins.options.weather.place` in their settings); the slide says so if there is none.
- `days`: 1 to 7 (default 5). `show`: `all` (default), `now`, `days` or `trend`, for a slide with
  room for less. `units`: `metric` (default) or `imperial`.
- Parts for focus cues: `now`, `trend`, `today` and the days by short name — `[[focus wx.sat]]`.

Before narrating it, `read_element` it: it answers in words with the conditions, each day, the
UV and the light, as of when it was fetched. `weather_now` answers the same for any place
without putting it on a slide.
