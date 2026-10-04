A `timezones` element is a 24-hour timeline with one band per place: each hour shaded by what it
means there — asleep, early, working, evening — with a line at now and each place's clock beside
its name. It keeps time by itself while the slide is up, so it is the thing to show whenever
the question is "what time is it there", "when can we meet", or "is it too late to call".

- `zones`: the places, first being home — the timeline is home's day, midnight to midnight.
  Each is an IANA zone id (`"Europe/Zagreb"`) or `{"zone": "America/New_York", "label": "New York"}`;
  without a label the city part of the id is used. Leave `zones` out to use the ones in the
  user's settings (`plugins.options.timezones.zones`), or the machine's own zone.
- `at`: a moment to mark with a dashed line, as local time in the home zone —
  `"2026-10-06T15:00"` — for "what time is the 3pm call for everyone".
- Each place is a part: `[[focus clocks.zagreb]]` lights its band and says when its clocks
  next change.

Read it back with `read_element` before narrating times: it answers with each place's time now
and the gap from home. `timezones_now` answers the same for any zones without a slide.
