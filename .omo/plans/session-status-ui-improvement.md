# Plan: Session Status UI and Buttons Improvement

This plan improves the "Session Status" window and its buttons by grouping commands by type, introducing interactive tabs/filters, and simplifying the buttons into a single row per command.

## TODOs

- [x] 1. `IKeyboardBuilder.cs`: Update `GetSessionCommandsKeyboardAsync` signature to accept `selectedFilter` string - expect compilation to succeed after updating implementation
- [x] 2. `KeyboardBuilder.cs`: Implement `GetSessionCommandsKeyboardAsync` with dynamic tab buttons and single-row action buttons - expect keyboard to show tabs and compact buttons
- [x] 3. `SessionManagementHandler.cs`: Update `HandleSessionDetailsAsync` to parse `sessionId` and `filter` from callback argument - expect correct tab filtering
- [x] 4. `SessionManagementHandler.cs`: Update `BuildStatusReply` to group commands by type and format as a tree-like structure - expect beautiful grouped markdown output
- [x] 5. `SessionManagementHandler.cs`: Update delete/cancel confirmation and execution handlers to pass and preserve the active filter - expect active tab to persist after actions

## Final Verification Wave

- [x] F1. Code Quality Reviewer (Oracle) - expect clean code, no stubs, proper nullability
- [x] F2. Functional Integrity Reviewer (Oracle) - expect correct tab switching, correct command cancellation/deletion, and correct back navigation
- [x] F3. Visual & UX Reviewer (Oracle) - expect beautiful tree-like markdown formatting and compact, self-explanatory buttons
- [x] F4. Build & Compilation Reviewer (Sisyphus-Junior) - expect `dotnet build TelegramBot.slnx` to pass with zero errors
