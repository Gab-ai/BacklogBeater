# Backlog Beater — Playnite Extension

A sidebar plugin for Playnite that dynamically recommends games from your unified library
(Steam, Epic, JAST, GOG, etc.) based on your actual playtime patterns.

## What it does

- **Analyses your playtime** across every platform Playnite has imported
- **Scores unplayed/barely-played games** by how well they match genres, tags, and
  features of titles you've sunk hours into
- **"Worth finishing"** section — games you started but dropped (1–5 hours played)
- **Recently played** — quick access to your last 5 sessions
- **Filter bar** — search recommendations by name, genre, or tag
- **Auto-refreshes** when a game session ends or your library updates
- **Double-click any card** to launch the game instantly

## Requirements

- Playnite 9 or 10 (Desktop mode)
- Visual Studio 2022 **or** Visual Studio Build Tools with .NET Framework 4.6.2 workload
- NuGet package restore (happens automatically on build)

## Build

```
1. Open GameRecommender.csproj in Visual Studio (or run `dotnet restore` then `dotnet build`)
2. Build in Release configuration
3. Output is in:  bin\Release\net462\
```

## Install

### Option A — Developer load (easiest for personal use)

1. Build the project (see above)
2. Open Playnite → Main Menu → Settings → For Developers
3. Under **External extensions**, click **Add** and point it to your build output folder:
   `C:\path\to\GameRecommender\bin\Release\net462\`
4. Restart Playnite
5. Look for the gamepad icon in the left sidebar

## Settings and optional services

Open Playnite -> Add-ons -> Extension settings -> Backlog Beater after install.

Most features work without API keys. The extension will still score your local Playnite
library, remember rejected recommendations, and apply manual blacklists. External keys
only improve metadata, not-owned discovery, deals, or optional AI re-ranking.

### Core enrichment

- **Steam API key + Steam User ID**: optional, but recommended. Enables Steam tag/taste
  data, Steam graph signals, and stronger Steam-backed discovery.
- **IGDB client ID + secret**: optional. Enables IGDB themes, keywords, websites, and
  richer metadata. Register through Twitch developer tools at `dev.twitch.tv`.
- **CurseForge API key**: currently exposed as a legacy compatibility field. Current
  modpack scanning and suggestions do not use it.

### AI re-ranking

AI re-ranking is off by default and costs API credits when enabled.

- **Anthropic API key**: required only when AI re-ranking is enabled with Claude.
- **OpenAI API key + model**: required only when AI re-ranking is enabled with OpenAI.
- **Candidates sent to AI** controls how many already-scored recommendations are sent
  for re-ranking.

### Deals

- **IsThereAnyDeal API key**: optional. Used only when you click the deals button.
- **JAST Store** and **MangaGamer** toggles add optional sale sources checked on demand.

### Minecraft modpacks

Standard Windows Prism, Modrinth, and CurseForge launcher paths are scanned
automatically. Use **Minecraft launcher paths** only for portable launchers or custom
instance folders. Separate multiple paths with semicolons.

### Advanced controls

Scoring engine weights and novelty strength are under **Advanced recommendation
controls**. The three engine weights must sum to 1.0.

### Option B — Package as .pext

1. Download Toolbox from https://playnite.link/docs/toolbox.html
2. Run:
   ```
   Toolbox.exe pack "C:\path\to\GameRecommender" "C:\output"
   ```
3. This creates a `GameRecommender_1.0.pext` file
4. Double-click the `.pext` file — Playnite will install it automatically

## How recommendations work

The engine builds a **taste profile** from every game you've played more than 5 hours:

- Each game's genres, tags, and features are weighted by **log(playtime)** — so a
  200-hour game influences the profile more than a 10-hour game, but doesn't completely
  dominate it
- Candidate games (unplayed or <5h) are scored against that profile
- Community score and critic score provide a small secondary boost
- The top 40 matches are shown, sorted by score

The profile updates live — every time you finish a session, the sidebar refreshes with
new data.

## Folder structure

```
GameRecommender/
├── extension.yaml          — Playnite manifest
├── GameRecommender.csproj  — Project file (.NET 4.6.2)
├── GameRecommenderPlugin.cs — Entry point, sidebar registration
├── RecommendationEngine.cs  — Pure scoring logic
├── RecommenderViewModel.cs  — Data binding / state
├── RecommenderView.xaml     — WPF sidebar UI
├── RecommenderView.xaml.cs  — Code-behind
└── Converters.cs            — WPF value converters + RelayCommand
```

## Extending it

Want to add a new scoring signal? Edit `RecommendationEngine.cs` → `ScoreGame()`.
Common additions:

- **Developer/publisher match** — if you loved a studio's last game, boost their others
- **Release year** — prefer newer games
- **User score threshold** — filter out games below a minimum rating
- **Completion time** — prefer games that match your typical session length (HLTB data
  via a metadata plugin)
