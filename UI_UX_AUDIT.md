# Satisfactory Mod Manager — Ruthless UI/UX Audit

> Scope: current `main` at v0.5.3, focused on the WinForms desktop experience in `Program.cs` and `ModernUi.cs`.
>
> Product conversion definition for this app: **launch → game detected → user understands how mods get into the app → first mod installed/recognized → confidence that the mod is active → launch Satisfactory**.
>
> This is intentionally not a polite design review. The standard is: the product should feel intentional, trustworthy, fast, and obvious on first contact — not like a collection of technically working controls given a modern theme.

---

# Implementation status

The first implementation pass is now applied in the application code: library-first layout, search/filtering, contextual mod actions, collapsed activity log, advanced settings moved out of the main workspace, direct update actions, explicit SML setup, quieter visual effects, responsive primary columns, improved setup recovery, and CI build verification.

The remaining unchecked items in this audit should be treated as follow-up refinement rather than blockers for the structural redesign.

---

# Executive diagnosis

The recent visual work improved surface polish, but the product is still organized like a developer control panel.

The central problem is not color, radius, glow, or typography. It is **hierarchy**.

The interface gives roughly equal visual weight to:

- installing mods;
- enabling/disabling/removing mods;
- checking updates;
- opening ficsit.app;
- choosing an executable;
- switching executable detection back to automatic;
- opening the Mods folder;
- opening the application's data folder;
- registering the protocol;
- switching theme;
- preferring Steam launch;
- choosing the game directory;
- launching the game;
- reading an always-visible activity console.

That is far too much responsibility for one screen with almost no progressive disclosure.

A good mod manager should make the normal path feel almost embarrassingly simple:

1. Satisfactory found.
2. Install mods from ficsit.app or drag a package here.
3. See what is installed and whether it is active/up to date.
4. Launch game.

Everything else is recovery, configuration, or power-user tooling and should visually recede until needed.

The application currently looks more sophisticated than it behaves, which is dangerous: visual polish raises expectations. Once a user sees a large polished card UI, they expect the information architecture, resizing, empty states, status feedback, and action model to be equally deliberate. They are not yet.

---

# CRITICAL

## C1 — The primary user journey has no dominant primary action

**Pass:** Designer + first-time user  
**Where:** `MainForm.BuildUi()`, header, MODS card, empty state  
**Problem:** The largest and most visually dominant action is **JOGAR**, despite the application being a mod manager and despite a first-time user likely having no mods configured yet.

The app's actual activation event is not "launch Satisfactory." It is "successfully install/manage a mod and understand that it will work." The UI currently promotes the end of the journey before establishing the beginning.

### First-time-user reaction

> "Okay, giant JOGAR button. Am I supposed to press this first? Does this install the mod loader? Where do I actually get mods? Is this a launcher with mod features, or a mod manager with a launcher?"

The empty-state copy mentions drag-and-drop and ficsit.app, but this guidance appears far below the dominant launch CTA and competes with many toolbar actions.

### Required fix

Rebuild the top-level hierarchy around **Library** and **Get Mods**.

Recommended shell:

- Top bar: product identity + game connection status + compact Play button.
- Main content: installed mods library.
- Primary CTA above/inside library: **Browse mods on ficsit.app**.
- Secondary CTA: **Install from file**.
- Drag-and-drop remains a convenience, not the only discoverable installation model.
- Game launch becomes prominent only after the game path is valid; it should not dominate onboarding.

When there are zero mods, replace the normal library with a purposeful onboarding empty state:

**No mods installed**
- **Browse mods on ficsit.app** — primary
- Install from file — secondary
- "You can also drag .zip/.smod packages here."

### Acceptance criteria

- A new user can identify how to get their first mod within 3 seconds.
- "Play" is not the biggest element on an empty library.
- The empty state has one obvious primary action.
- No knowledge of `smmanager://`, package formats, or folders is required to begin.

---

## C2 — Responsive behavior is structurally unsafe and can clip core controls

**Pass:** Designer  
**Where:** `BuildUi()` fixed row heights and wrapped `FlowLayoutPanel` action groups  
**Evidence:** The root reserves 142 px for the two action cards. Both cards use `WrapContents = true`, `AutoScroll = false`, and contain many variable-width controls.

This is a classic "looks correct on my machine" layout.

At the 980 px minimum width, the two cards are approximately 56/44% of available width. The MODS card must wrap up to nine actions. The game/utilities card contains six buttons plus a checkbox. Their vertical demand is content-dependent, but the parent row is fixed at 142 px and refuses scrolling.

That means font scaling, translated labels, DPI scaling, Windows text metrics, or narrower resizing can clip or hide actions.

### Required fix

Do not solve this by increasing the fixed height.

Replace the action-card control dump with a stable hierarchy:

- Put common item actions in a compact toolbar immediately above the list.
- Move rare utility actions to a Settings/overflow menu.
- Use responsive layout rules, not arbitrary fixed vertical capacity.
- At minimum, switch action regions to auto-sized rows with a sane maximum and explicit overflow handling.

Test at:
- 980×640
- 1180×760
- 1366×768
- 150% Windows scaling
- 200% Windows scaling

### Acceptance criteria

- No actionable control is clipped at minimum supported size.
- No action disappears because labels wrap.
- 150% DPI does not collapse the main library into unusability.
- Resizing produces intentional reflow, not accidental wrapping.

---

## C3 — The mod table is wider than the app and wastes prime space on low-value data

**Pass:** Designer + first-time user  
**Where:** ListView columns in `BuildUi()`  
**Evidence:** Column widths total roughly 1322 px:

- Status 96
- Nome 260
- Versão 110
- Atualização 190
- Tipo 170
- Arquivos 76
- Origem 420

The default window width is 1180 px before padding. Horizontal scrolling is therefore baked into the default experience.

Worse, **Origem** receives 420 px — more than the mod name — even though raw source paths are diagnostic metadata, not a primary user decision.

### First-time-user reaction

> "Why is half the table file paths? What am I supposed to do with 'Origem'? Where are the things I care about — enabled, version, update?"

### Required fix

Design the list around user decisions, not database fields.

Default visible columns:

- Mod
- Status
- Installed version
- Update

Optional/detail metadata:
- type
- file count
- source path

Move secondary metadata into a right-side details panel, expandable row, context menu, or Properties dialog.

Also:
- Make Name elastic/fill remaining width.
- Keep Status compact.
- Turn update state into a concise visual status with an action when applicable.
- Never require horizontal scrolling for the primary columns.

### Acceptance criteria

- The primary library has no horizontal scrollbar at default/minimum width.
- A user can tell mod name, enabled state, version, and update state without scrolling.
- Raw paths are not always visible.

---

## C4 — "Check for updates" detects a problem but does not complete the job

**Pass:** First-time user  
**Where:** `CheckSelectedModUpdate`, `CheckAllModsForUpdates`, `OpenSelectedModPage`  
**Problem:** The app can determine that a mod is outdated, but the workflow terminates in "Use Página do mod to download."

This creates an avoidable conversion cliff:

1. user asks the manager whether a mod needs updating;
2. manager says yes;
3. manager sends user out to a browser;
4. user must choose/download the correct version;
5. user must return and install it.

The application already has ficsit.app download/install infrastructure. From the user's perspective, "update available" should lead to **Update**, not research.

### Required fix

When a valid SMR identity and compatible downloadable package are available:

- replace passive status with **Update to X.Y.Z**;
- support one-click update;
- support **Update all** for resolvable updates;
- show progress inline;
- preserve fallback "Open on ficsit.app" only when automatic update cannot be completed.

If compatibility cannot be proven, say exactly why and offer the safe fallback.

### Acceptance criteria

- Most supported mods can be updated without leaving the app.
- "Update available" has a direct next action.
- Batch update is available when multiple resolvable updates exist.
- Browser fallback is the exception, not the default workflow.

---

## C5 — Launching the game can silently trigger a network installation of SML

**Pass:** First-time user  
**Where:** `LaunchGame()` → `EnsureSmlInstalledAsync()`  
**Problem:** Pressing **JOGAR** can automatically download and install SML if it is missing.

That may be technically convenient, but UX-wise this is a trust violation because the visible action says "play", not "modify my game installation by downloading a dependency."

The operation is only represented in the log/progress bar and can happen before the game launches.

### First-time-user reaction

> "I clicked Play. Why is it downloading something into my game? Did it just change files without asking?"

### Required fix

Make SML status a first-class setup state.

If mods require SML and it is missing:

- show a setup card/banner: **Satisfactory Mod Loader required**;
- explain in one sentence what it is;
- primary action: **Install SML**;
- after success, state becomes **Ready**.

If Play is pressed before setup is complete, open that setup step instead of silently mutating the install.

### Acceptance criteria

- No substantial game modification is hidden behind the word "Play."
- User sees SML status before launching modded Satisfactory.
- Automatic installation occurs only after an explicit action that names what will be installed.

---

## C6 — Setup state is represented as a path textbox instead of a product state

**Pass:** Designer + first-time user  
**Where:** header game path row, `AutoDetectGame`, `UpdateGameStatus`  
**Problem:** A raw editable filesystem path is permanently promoted into the header.

For normal users, the question is not "what string is in GameRoot?" It is:

- Is Satisfactory detected?
- Which installation/store?
- Is it ready for modding?
- If not, what should I do?

The current design exposes the implementation rather than presenting the outcome.

### Required fix

Replace the always-visible path editor with a compact connection state:

**Satisfactory**
- ✓ Detected via Steam
- `D:\SteamLibrary\...\Satisfactory`
- Change…

When missing:

**Satisfactory not detected**
- **Find automatically**
- Choose folder…

The raw path can appear in smaller secondary text and become editable only through Change/Choose.

### Acceptance criteria

- Valid game setup looks like a completed state, not a form field.
- Invalid setup has one clear repair action.
- Users do not need to understand "FactoryGame" until automatic detection fails.

---

## C7 — Rare settings and recovery actions are polluting the primary workspace

**Pass:** Designer  
**Where:** "JOGO E UTILITÁRIOS" card  
**Problem:** These controls are visible all the time:

- Selecionar executável...
- Auto
- Abrir Mods
- Abrir dados
- Registrar ficsit.app
- Modo claro/escuro
- Preferir iniciar via Steam

This is not a coherent workflow. It is a junk drawer.

It forces first-time users to parse troubleshooting and configuration vocabulary before they have installed a mod.

### Required fix

Create a real **Settings** surface.

Move into Settings:
- launch executable override;
- prefer Steam;
- theme;
- app data folder;
- protocol registration/repair.

Move into overflow/context menu:
- Open Mods folder.

Keep main workspace limited to:
- game readiness;
- get/install mods;
- library;
- mod actions;
- launch.

### Acceptance criteria

- No more than 2–3 top-level actions compete with the library.
- Recovery/debug controls are not visible until requested.
- "Auto" never appears as an unlabeled standalone action in the primary UI.

---

# HIGH IMPACT

## H1 — The interface has a theme, but not a product visual language

**Pass:** Designer  
**Where:** `ModernUi.cs`

The moving glow, grid, gradients, rounded cards, spotlight hover, pills, and uppercase section labels are individually defensible. Together they read as "modern UI effects applied to WinForms."

Linear, Raycast, Superhuman, and Vercel do not feel premium because they maximize effects. They feel premium because they aggressively control visual hierarchy and remove decoration that does not carry information.

The animated ambient background is especially suspect: it consumes implementation complexity and visual attention while core interaction states remain ordinary WinForms controls.

### Required fix

Reduce decorative layers by roughly half.

Keep:
- coherent dark/light palette;
- strong spacing;
- one radius system;
- subtle elevation;
- restrained accent;
- strong selection/focus states.

Remove or drastically soften:
- drifting glow blobs;
- decorative grid;
- pointer-follow spotlight on every card;
- unnecessary gradients/highlights.

Use the visual budget on:
- better empty states;
- clearer selected row;
- update badges/actions;
- readiness states;
- install progress;
- errors.

---

## H2 — The information architecture is "controls first," not "objects first"

**Pass:** Designer  
**Problem:** The MODS section is a large pile of global buttons before the user reaches the actual mod objects.

Object-management products are strongest when the object list is primary and actions appear in context.

### Required fix

Make the library the largest uninterrupted region.

Suggested top of library:

**Mods**  
`Search installed mods…` | **Browse mods** | **Install file** | `⋯`

When a row is selected, expose actions in a contextual command bar or details pane:
- Enable/Disable
- Update
- Open page
- Remove
- More…

This eliminates disabled-button clutter when nothing is selected.

---

## H3 — Disabled action buttons advertise irrelevant complexity

**Pass:** First-time user  
**Where:** `UpdateButtons()`

With no selection, Enable, Disable, Remove, Check update, and Open page are all visible but disabled.

Disabled controls still create cognitive load. They tell the user "there are five things here you should eventually understand."

### Required fix

Use contextual actions only after selection. Do not render a graveyard of disabled commands.

---

## H4 — Double-click to toggle enabled state is too destructive/opaque

**Pass:** First-time user  
**Where:** `_mods.DoubleClick += (_, _) => ToggleSelected();`

Double-click normally means open/details, especially in Windows list interfaces. Here it changes installation state.

That is a hidden behavior with consequences.

### Required fix

Use an explicit switch/toggle in the Status column or a clear Enable/Disable control.

Use double-click for details, or do nothing.

---

## H5 — The activity log is permanently consuming scarce vertical space

**Pass:** Designer + first-time user  
**Where:** fixed 118 px "REGISTRO DE ATIVIDADES" row

The activity log is implementation telemetry masquerading as primary UI.

Normal success feedback should not require users to read a console. Permanent logs make the product look unfinished and technical.

### Required fix

Collapse logs into:
- transient toast/status messages for normal operations;
- an Activity drawer/panel for history;
- expandable details for failures.

Keep logs accessible for troubleshooting, but not permanently open.

The recovered vertical space should go to the mod library.

---

## H6 — Success and failure feedback is split across three competing channels

**Pass:** First-time user  
**Channels:**
- MessageBoxes
- activity log
- bottom status/progress bar

Users should not have to learn which channel matters.

Examples:
- protocol registration uses a modal;
- external install success uses a modal;
- local installs primarily log;
- enable/disable failures primarily log;
- game path failures use modals;
- progress is shown in a generic bottom marquee.

### Required fix

Establish feedback rules:

- Toast/banner: completed non-destructive action.
- Inline row state: per-mod work/status.
- Modal: only decisions, confirmations, or blocking recovery.
- Activity drawer: diagnostic history.

Do not use a modal merely to announce success.

---

## H7 — Progress is global and contextless

**Pass:** First-time user  
**Where:** bottom marquee progress bar

A small anonymous marquee at the bottom gives no answer to:
- what is happening?
- which mod?
- how long?
- can I cancel?
- did it succeed?

### Required fix

For installs/updates:
- show operation name;
- show mod name;
- determinate progress when possible;
- bind status to the affected item;
- disable only conflicting actions, not the entire mental model.

For short metadata checks, use inline spinner/state in the update column.

---

## H8 — Update status uses green for "update available"

**Pass:** Designer  
**Where:** `Mods_DrawSubItem`, update column

Green conventionally means good/current/success. An available update is an attention state, not a success state.

### Required fix

Use:
- muted/green check for Up to date;
- accent/blue or amber for Update available;
- warning for failed check;
- spinner/subtle animated state for Checking.

Do not make "you are outdated" look like success.

---

## H9 — "Ativo / OFF" mixes language and visual tone

**Pass:** Designer  
**Where:** Status pill

The app is Portuguese-first, but the disabled label is "OFF". This is minor text by itself, but it contributes to the assembled-from-components feeling.

### Required fix

Use a consistent pair:
- Ativo / Desativado
or
- Ligado / Desligado

Prefer "Ativo / Desativado" for mod semantics.

---

## H10 — Emoji are being used as UI iconography

**Pass:** Designer  
**Where:** buttons, context menu, chips

Examples include `▶`, `＋`, `↻`, `🧩`, `✅`, `🌐`.

Emoji rendering varies by Windows version/font and produces inconsistent visual weight. It also makes an otherwise restrained UI feel less native and more prototype-like.

### Required fix

Use one icon system:
- Segoe Fluent Icons/Symbols where reliable, or
- small monochrome vector/raster resources bundled with the app.

Icons should be optional reinforcement, never the only semantic cue.

---

## H11 — The application has no search/filter once the library grows

**Pass:** First-time user  
**Problem:** Installed mods are sorted alphabetically, but there is no search, status filter, update filter, or sort model exposed.

At 5 mods this is fine. At 50+ it becomes a list dump.

### Required fix

Add:
- search by mod name;
- filter: All / Active / Disabled / Updates;
- sortable Name / Version / Update columns if ListView remains.

The controls should be compact and live directly above the library.

---

## H12 — Protocol integration is technically central but psychologically unclear

**Pass:** First-time user  
**Problem:** The app auto-registers `smmanager://`, exposes a manual registration button, and tells users that clicking Install on ficsit.app will route here.

A user may reasonably ask:
- Is integration active now?
- Will Windows ask which app to open?
- Did another mod manager take over the protocol?
- Why is there a "register" button if it registered itself automatically?

### Required fix

Treat protocol integration as a status, not an unexplained command.

Settings → Integration:

**ficsit.app integration**
- Connected / Needs repair / Unknown
- "Lets Install buttons on ficsit.app open this manager."
- Repair integration

Main onboarding can say: "Browse on ficsit.app; Install will return here."

---

## H13 — The header title is styled like breadcrumb branding but carries no useful navigation

**Pass:** Designer  
**Where:** `SATISFACTORY / MOD MANAGER v0.5.3`

The slash and all-caps styling imply navigation hierarchy, but it is static branding. It spends visual emphasis without adding orientation.

### Required fix

Use a restrained app title. Put version in About/Settings or a low-priority footer/tooltip.

Header space should communicate game readiness and current context.

---

## H14 — "Source" and file count are diagnostic metadata pretending to be product metadata

**Pass:** First-time user  
**Problem:** "Arquivos" and "Origem" are prominent columns even though users generally care about mod identity, state, compatibility, and update status.

### Required fix

Move them to Details:
- Source
- installed files
- mod type
- filesystem paths
- backup information

This also makes room for a better mod-name column and update action.

---

# NICE TO HAVE

## N1 — Add mod details instead of opening directly to the browser

**Pass:** First-time user

A selected mod could show:
- name;
- installed/latest version;
- enabled state;
- source;
- ficsit.app link;
- files/details;
- update action;
- remove action.

A compact side panel would eliminate several global buttons and make the application feel object-oriented rather than toolbar-oriented.

---

## N2 — Replace theme toggle text with a normal Settings preference

**Pass:** Designer

"Modo claro"/"Modo escuro" as a persistent primary action is UI chrome managing UI chrome.

Use Settings:
- System
- Dark
- Light

Default to System if possible.

---

## N3 — Preserve and restore table state intentionally

**Pass:** First-time user

If column widths, sorting, search/filter state, or selected mod become richer, persist sensible preferences. Do not persist transient diagnostic states.

---

## N4 — Add keyboard shortcuts where they match Windows expectations

**Pass:** Designer

Examples:
- Ctrl+O: install file
- Ctrl+F: search mods
- Delete: remove selected, with confirmation
- Enter: details/open selected
- Space: toggle enabled state if explicit and discoverable
- F5: refresh/check state

Show shortcuts in menus/tooltips rather than requiring memorization.

---

## N5 — Make destructive removal visually and verbally precise

**Pass:** First-time user

The current confirmation is reasonable but still bundles "remove from manager and game" into one action.

Consider:
- **Uninstall mod** as the primary wording;
- optional separate "Forget record" only if it serves a real recovery use case;
- detail whether backups will be restored.

The command should match the user's mental model: they are uninstalling a mod, not "removing a database record."

---

## N6 — Add a compact readiness summary

**Pass:** First-time user

Near the top:

- Satisfactory: Ready
- SML: Installed
- Mods: 14 active
- Updates: 2 available

This compresses four separate concepts into a trustworthy snapshot.

Do not turn these into decorative metric cards; one compact status line is enough.

---

## N7 — Use native/reduced motion expectations more broadly

**Pass:** Designer

The animated background respects Windows menu-animation preference as a proxy for reduced motion, which is better than ignoring motion preferences. But the safer design is to make ambient animation unnecessary.

If it remains:
- keep extremely slow/subtle;
- pause when window is not active;
- stop when minimized;
- avoid continuous repaints unless they visibly improve the product.

---

# PASS 1 — VISUAL / PRODUCT DESIGNER WALKTHROUGH

This is the consolidated designer pass: what the app communicates before I care whether the functions work.

## First frame

I see a dark, high-contrast, card-based desktop app with an animated gradient/grid treatment. The styling is trying very hard to announce "premium/modern."

Then the content says something else: path textbox, utility buttons, raw filesystem metadata, console log, checkboxes, protocol registration, "Auto," file count, source paths.

That mismatch is the first problem.

A mature product hides its machinery until I need it. This app presents machinery inside attractive containers.

## Header

The header gives disproportionate space to the game path and Play.

The path is configuration, not identity. Play is downstream, not the core task. The best content — the mod library — starts substantially lower in the window.

The header should answer one question: **Is my Satisfactory installation ready?**

If yes, compress it.
If no, help fix it.

## Action cards

The two action cards are the biggest "AI/vibe-coded" tell.

Why? Because they are category buckets containing buttons, not designed workflows.

The MODS card is a row-wrap of every verb anyone thought of.
The JOGO E UTILITÁRIOS card is a second row-wrap of every remaining verb.

This is how control panels grow. It is not how focused products are organized.

The fix is not prettier buttons. The fix is deleting most of the always-visible buttons.

## Summary chips

"X mods cadastrados" and "Y ativos" are fine information but do not deserve decorative chips above the library unless they help a decision.

A more valuable summary would include updates or readiness. If the chip system remains, do not let metrics become ornamental dashboard furniture.

## Library

This should be the hero.

Instead, it is visually boxed between action chrome above and a console below.

The table exposes too many implementation fields and forces width pressure. The list should feel calm and dense: name, state, version, update. Everything else on demand.

Selection should produce contextual commands. The current global-button model is backwards.

## Activity console

This is useful for the developer and occasionally useful for a power user. It is not worth 118 persistent vertical pixels.

A premium tool can expose logs without looking like a debugging application.

## Background effects

The background is over-designed relative to the interaction design.

The app would look more professional with a flat, controlled surface and excellent spacing than with a moving spotlight/grid behind a cluttered command hierarchy.

If an effect has no semantic function and competes with unresolved hierarchy, remove it.

---

# PASS 2 — FIRST-TIME END USER WALKTHROUGH

Assume I downloaded this because I want mods for Satisfactory. I have not read the README.

## Launch

The window opens.

I immediately see:
- a game path;
- Detect;
- Browse;
- a huge Play button;
- a checkbox about launching after install;
- two cards full of buttons.

I do not yet know what the recommended path is.

My first instinct is that this might be primarily a game launcher, because Play is the loudest action.

## Finding the game

If auto-detection succeeds, I still see a path textbox as if I am expected to verify or edit it.

If detection fails, the app tells me to choose the root folder containing FactoryGame. That is technically accurate, but now I am thinking about installation directory internals before I have done anything useful.

I would prefer:
- "Satisfactory not found"
- "Find automatically"
- "Choose installation folder"

Only tell me about FactoryGame after I choose the wrong folder.

## Getting a mod

I scan the MODS card.

"Adicionar mod" sounds like I already possess a mod file.
"Adicionar pasta" definitely assumes I already possess files.
"Página do mod" is disabled because I have not selected one.
The help text eventually tells me I can use ficsit.app.

The thing I actually want — **find mods** — is not presented as the primary action.

This is the moment I would be most likely to leave the app and just use the official manager or browser workflow.

## Empty library

The empty-state text is directionally good, but it is text where there should be an action.

Give me a button to ficsit.app. Teach the integration by using it.

"Browse mods" is understandable.
"Register ficsit.app" is not.

## Installing

If I drag a package in and it succeeds, the permanent log tells me.

I should not need to read a timestamped console to know whether the primary task worked.

The new mod should visibly appear with:
- installed successfully;
- active;
- version;
- possibly a brief toast.

## Selecting a mod

When I select a mod, several previously disabled buttons become enabled.

This works, but it makes me scan the entire card again to discover what changed.

Actions should appear near the selected mod or in a details area.

Double-click unexpectedly toggles enabled/disabled. That is surprising enough that I would assume I accidentally changed something.

## Checking updates

I can check one mod or all mods.

If an update exists, the app tells me to use Page of mod to download it.

This feels unfinished because the manager clearly already knows:
- which mod;
- installed version;
- latest version;
- its ficsit identity.

I expect an Update button.

## Playing

I click Play.

If SML is missing, the manager may download/install it automatically before launching.

That is useful once I trust the tool, but alarming if I do not. The interface should tell me whether the modding runtime is ready before I click Play.

## Troubleshooting

Now the utility buttons become useful:
- select executable;
- auto;
- prefer Steam;
- open folders;
- register integration.

The problem is not that these features exist. The problem is that I had to look at them from the first second.

They belong in Settings/Advanced/Troubleshooting.

---

# Target information architecture

A concrete direction for implementation:

```text
┌─────────────────────────────────────────────────────────────────────┐
│ Satisfactory Mod Manager                  Satisfactory ✓ Steam  Play │
├─────────────────────────────────────────────────────────────────────┤
│ Mods                                                                │
│ [ Search installed mods... ]  [ Browse mods ] [ Install file ] [⋯] │
│                                                                     │
│ All   Active   Disabled   Updates (2)                               │
│ ┌─────────────────────────────────────────────────────────────────┐ │
│ │ Mod                    Status       Version       Update         │ │
│ │ Area Actions           Active       2.1.0         Up to date     │ │
│ │ Infinite Zoop          Active       1.4.2         Update 1.5.0   │ │
│ │ Refined Power          Disabled     3.0.1         Up to date     │ │
│ └─────────────────────────────────────────────────────────────────┘ │
│                                                                     │
│ Selected: Infinite Zoop                          [Disable] [Update]  │
├─────────────────────────────────────────────────────────────────────┤
│ ✓ Ready · SML installed · 3 active · 2 updates              Activity│
└─────────────────────────────────────────────────────────────────────┘
```

Settings / overflow:
- Game installation
- Launch method
- ficsit.app integration
- Theme
- Open Mods folder
- Open app data
- Activity/log
- About/version

Empty state:

```text
No mods installed

Browse community mods on ficsit.app and click Install.
This manager will receive the package automatically.

[ Browse mods on ficsit.app ]

or [ Install from file ]
You can also drag .zip/.smod packages into this window.
```

Missing SML state:

```text
Mod loader required

Satisfactory Mod Loader is required to run installed mods.
[ Install SML ]

This downloads SML from ficsit.app and installs it into your Satisfactory installation.
```

---

# Implementation order for Claude Code

## Phase 1 — Fix hierarchy before styling

1. Remove the two permanent action-card button dumps.
2. Make the mod library the dominant central region.
3. Add a library command row: Search, Browse mods, Install file, overflow.
4. Move utilities/settings out of the main screen.
5. Collapse the activity log behind an Activity action/drawer/dialog.
6. Replace the always-editable game path with a game connection/readiness component.
7. Make the empty library an onboarding surface with a real Browse Mods CTA.

Do not spend time adjusting colors until this phase is complete.

## Phase 2 — Fix object interaction

1. Reduce default columns to Mod / Status / Version / Update.
2. Add contextual selected-mod actions.
3. Remove double-click toggle behavior.
4. Add search and All/Active/Disabled/Updates filters.
5. Make update status semantics consistent.
6. Replace emoji with a coherent icon source.

## Phase 3 — Complete broken loops

1. Add explicit SML readiness/setup instead of hidden install-on-Play behavior.
2. Add direct per-mod update where API/package resolution permits.
3. Add Update All where safe.
4. Convert routine success/failure feedback away from console reading.
5. Make progress contextual.

## Phase 4 — Remove ornamental noise

1. Reduce/disable moving background effects.
2. Remove decorative grid unless it survives comparison against a flat version.
3. Tone down card spotlight effects.
4. Standardize spacing, typography, and focus/selection states.
5. Run DPI/resizing tests.

---

# Definition of done

Do not call the redesign complete until all of these are true:

- [ ] A brand-new user can identify "how do I get a mod?" immediately.
- [ ] Empty state contains a primary Browse Mods action.
- [ ] Satisfactory detection is shown as a readiness state, not an always-editable path form.
- [ ] The main screen no longer exposes executable override, protocol repair, theme, app-data folder, and Steam preference.
- [ ] The library is the largest content region.
- [ ] No primary workflow requires reading the activity log.
- [ ] Primary mod information fits without horizontal scrolling at the minimum window width.
- [ ] No controls clip at 980×640.
- [ ] No controls clip at 150% Windows scaling.
- [ ] A selected mod exposes actions contextually.
- [ ] Double-click does not silently toggle installation state.
- [ ] Update available is not colored as a success state.
- [ ] Supported updates can be completed directly in-app.
- [ ] Missing SML is disclosed before installation rather than silently handled by Play.
- [ ] Progress identifies what operation is running.
- [ ] Routine success does not require a MessageBox.
- [ ] Logs remain accessible for troubleshooting but are not permanently visible.
- [ ] Decorative animation can be removed without harming hierarchy; if removing it improves the product, remove it.
- [ ] The normal path contains no unexplained terms such as "Auto", "smmanager://", "FactoryGame", or "app data".
- [ ] The visual hierarchy still works in both dark and light mode.

---

# Final product principle

**Stop designing the interface around everything the program can do. Design it around what the user is trying to accomplish right now.**

The current app has enough functionality to be useful. The redesign should make most of that functionality temporarily invisible.

That is the difference between a polished utility and a polished control panel.
