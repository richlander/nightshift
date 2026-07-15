<!--
Title: describe the change in plain language (what it does), NOT the order id.
The order↔PR mapping is carried by the branch name (nightshift/<plan>/<order>)
and the Nightshift-Order: commit trailer — don't repeat it in the title.

Write for a reader with no prior context. Delete any section that genuinely
doesn't apply, but prefer to fill them: a substantive change earns a substantive
writeup. Keep validation/CI status and the adversarial-review verdict OUT of this
body — the review clearance lives in a sidecar comment.
-->

## Summary

<!-- What this change does, in a few sentences. Lead with the change itself. If there is a
     deliberate behavior change, call it out explicitly and separate it from pure refactoring. -->

## Why this matters

<!-- The problem, gap, or divergence this resolves, and why it's worth doing now. A few
     sentences or a short list — enough that the motivation is clear without reading the diff.
     Where it applies, note what the change unlocks downstream. -->

## What changed

<!-- The substance, grouped by area, as bullets. Flag behavior changes vs. refactoring.
     Enough that a reviewer can navigate the diff from this section alone. -->

## Order

Closes #<!-- issue number -->
