---
description: Commit all changes with an auto-generated message
---

Look at the current git state and create a commit.

Steps:
1. Run `git status` and `git diff` to understand what changed.
2. Run `git add -A` to stage everything.
3. Write a concise conventional commit message that accurately describes the changes (e.g. `feat:`, `fix:`, `refactor:`, `chore:`). Do NOT use generic messages like "wip" or "auto commit".
4. Run `git commit -m "<message>"`.
5. Confirm the commit was created with `git log -1 --oneline`.

If there is nothing to commit, just say so — do not create an empty commit.
