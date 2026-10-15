# Versionamento e rilasci

## Branching: trunk-based
- `main` e sempre rilasciabile. Si lavora su branch brevi (`feat/...`, `fix/...`) e si integra con PR in **squash merge**.
- Il titolo della PR diventa il commit su `main` e deve seguire Conventional Commits (controllato dal workflow `pr-title`).
- **Branch di manutenzione** solo quando serve una patch su una versione vecchia gia rilasciata: `release/X.Y` creato dal tag `vX.Y.0`, correzione, rilascio `vX.Y.Z+1`, poi cherry-pick su `main` se pertinente.

## Conventional Commits
| Tipo | Effetto sulla versione | Nel changelog |
|---|---|---|
| `feat:` | minor | Nuove funzionalita |
| `fix:` | patch | Correzioni |
| `perf:` | patch | Prestazioni |
| `feat!:` / `BREAKING CHANGE:` | major (minor prima della 1.0) | Evidenziato |
| `docs:`, `refactor:`, `test:`, `build:`, `ci:`, `chore:` | nessuno | Nascosti |

- Le correzioni di sicurezza usano `fix(security): ...`.
- Le feature nate da una spec la citano: `feat: primo avvio e prima risposta (spec 001)`.

## Versione: SemVer calcolata da git
- **MinVer** legge l'ultimo tag `vX.Y.Z`: la versione finisce in assembly, immagine Docker e `/api/version`.
- Tra un tag e l'altro la versione e una pre-release (es. `0.2.0-alpha.0.5`).
- Prima della 1.0 i breaking change incrementano la minor.

## Rilascio con release-please
1. Ogni push su `main` aggiorna una **PR di rilascio** con versione calcolata e `CHANGELOG.md`.
2. Fare merge di quella PR crea il tag `vX.Y.Z` e la GitHub Release con le note.
3. Il tag fa partire la build delle immagini (pipeline da definire).

## Cosa e "breaking" per Quorum
Richiede una major (minor prima della 1.0) un cambiamento incompatibile di:
- API pubblica;
- formato dichiarativo di agenti, tool, topologie;
- variabili d'ambiente o `docker-compose.yml`;
- formato delle chiavi di licenza;
- migrazioni del database senza percorso di aggiornamento automatico.

## Regole per i clienti self-hosted
- Migrazioni solo in avanti: nessun downgrade; documentare il backup prima di ogni aggiornamento.
- Tag immagini Docker: `X.Y.Z`, `X.Y`, `X`, `latest`.
- Versione visibile in UI e su `/api/version`.
- "What's new": le note della versione sono incluse nell'immagine e mostrate all'amministratore al primo accesso dopo un aggiornamento (spec futura).
