---
applyTo: "**/*.cs"
description: "Use when editing Unity C# scripts with serialized fields or inspector-assigned references. Treat serialized values as valid at use sites and establish or repair them in OnValidate instead of adding repeated null checks."
---

# Serialized Values

For Unity serialized fields and other inspector-assigned references:

- Assume the serialized value is valid when using it.
- Do not add repeated runtime null checks around serialized references just to be defensive.
- Use `OnValidate` to assign, repair, or verify serialized references so normal call sites can stay direct.
- If a serialized reference is required for the component to function, keep the enforcement close to setup code, not scattered across usage sites.
- Keep dynamic null checks only for values that are genuinely optional or can change independently at runtime.
