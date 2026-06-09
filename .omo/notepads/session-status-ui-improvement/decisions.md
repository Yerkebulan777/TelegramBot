# Decisions

## 2026-06-09 Task: session-status-ui-improvement
- **Decision 1**: Group commands by command type (e.g., PDF, DWG, NWC) in the detailed status message using a tree-like structure with Unicode box-drawing characters.
- **Decision 2**: Implement interactive tabs/filters on the keyboard using the callback format `SESSIONDETAILS:{sessionId}:{filter}` (e.g., `SESSIONDETAILS:15:PDF`).
- **Decision 3**: Simplify the commands keyboard to show exactly one row per command, combining the command type, filename, and action (Cancel/Delete) into a single clickable button.
- **Decision 4**: Pass the active filter through the delete/cancel confirmation flow (`DELETECOMMAND:{commandId}:{filter}` -> `CONFIRMDELETECOMMAND:{commandId}:{filter}`) to preserve the user's active tab after performing an action.


