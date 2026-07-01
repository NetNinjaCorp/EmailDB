---
name: pm-autoscope
description: Automated scoping — discover and create epics, stories, and tasks in bulk
user_invocable: true
---

# /pm-autoscope — Automated Scoping

Automates the discovery and creation pipeline for epics, stories, and tasks.

## How it works

1. Call `pm_auto_scope()` to detect the project state and get discovery signals.
2. Based on the mode returned, follow the appropriate workflow below.

## Full Scan Mode (new projects)

Triggered when no epics or stories exist yet. The tool returns codebase signals (docs, build files, source tree).

**Workflow:**
1. Analyze the codebase signals returned by `pm_auto_scope`
2. Propose 2-5 epics covering the major areas of work
3. Present the epic list to the user for approval — let them add/remove/edit
4. Create approved epics with `pm_create_epic`
5. For each epic, propose 2-6 stories
6. Present stories to the user for approval per epic
7. Create approved stories with `pm_create_story`, linking to the epic via `epic_id`
8. For each story, propose 2-6 tasks
9. Present tasks to the user for quick approval
10. Create approved tasks with `pm_create_task`
11. Show summary: N epics, N stories, N tasks created, total points

## Incremental Mode (existing stories need tasks)

Triggered when stories exist but some have no tasks. This is the primary use case.

**Workflow:**
1. `pm_auto_scope` returns the list of undecomposed stories with their bodies
2. **Loop through each story**:
   a. Call `pm_scope(story_id)` to get full decomposition context
   b. Propose 2-6 tasks for the story based on its content
   c. Present the task list to the user for quick approval (yes/edit/skip)
   d. Create approved tasks with `pm_create_task`
3. After all stories are scoped, show summary:
   - N stories scoped
   - N tasks created
   - Total points assigned
4. Suggest next steps: `/pm board` to see available work or `/pm-plan` for sprint planning

## Guidelines

- Keep task titles as verb phrases: "Add authentication middleware", "Write unit tests for parser"
- Each task should be 1-5 points (completable in one session)
- Tasks should be independently testable
- First task in a story sets up the foundation
- Last task handles integration/cleanup
- Don't over-decompose — 2-6 tasks per story is the sweet spot

## Forcing a mode

Users can force a specific mode:
- `/pm autoscope full` — force full codebase scan even if stories exist
- `/pm autoscope incremental` — force incremental even if no stories exist
