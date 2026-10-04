A `holidays` element lists the next public holidays in a country: the date, the name (and the
local name where the country has its own language), the regions each applies in, and how soon
it is. The home region's holidays are in full colour and the rest dimmed, because barely half of
some countries' holidays are national. It keeps itself current, so use it rather than writing
dates into markdown whenever the question is "when is the next long weekend", "why is nobody in
Sydney answering", or "what's closed over Christmas".

- `country`: a two-letter code (`AU`, `HR`, `DE`, `US`). Leave it out for the user's own, from
  their settings (`plugins.options.holidays`) or this machine's region.
- `region`: the home region, the country's own code for it without the country: `VIC`, `NSW`,
  `BY`. `only_region: true` leaves out the holidays that do not apply there.
- `count` (default 8) and `from` (`"2026-12-01"`) for a list that starts later than today.
- Each holiday is a part, by its name in lower case with hyphens: `[[focus hols.christmas-day]]`.

`read_element` it before narrating: it answers with each holiday, where it applies and how far
off it is. `holidays_upcoming` answers the same without a slide.
