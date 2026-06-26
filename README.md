# Issam's Tube

![CI](https://github.com/issam-hamlil/issam-s-tube/actions/workflows/ci.yml/badge.svg)

A personal video downloader for TikTok, X/Twitter, Facebook, and Instagram —
built to learn full-stack delivery end to end (backend API, containerization,
CI/CD, and a mobile client), not to run as a public service.

## Stack

- **Backend:** ASP.NET Core Minimal API (.NET), wrapping `yt-dlp` as a subprocess
- **Persistence:** SQLite + EF Core (download history)
- **Observability:** Serilog (console + rolling file)
- **Packaging:** Docker (multi-stage build), GitHub Actions CI/CD → GHCR
- **Mobile:** React Native (Expo)

## Architecture

![architecture diagram](docs/architecture.png)

Mobile app → `/extract` → `yt-dlp` subprocess → target platform's CDN.
Every call is logged to SQLite (`/history`) and to a structured log file.

## Running it

```bash
docker compose up -d
```

Requires a cookies.txt in backend/ if Instagram starts requiring login (see the Phase 3 notes in docs/) — it's gitignored, you generate your own.

## Why personal-use-only

This wraps yt-dlp, which itself just automates what a browser can already do for content you can already view. It's built for personal archiving of content from accounts I follow, run on hardware I own, not redistributed or exposed publicly — which is why there's no rate-limiting-for-strangers, no ToS-acceptance flow, and no app-store submission anywhere in this repo.
