---
name: pm-do
description: Execute a task — grab if needed, load context, implement, and complete
user_invocable: true
disable-model-invocation: true
args: "<task-id> [--complete]"
---

# /pm-do — Execute Task

## Flags

- `--complete` — Auto-close mode: skip the review option, mark the task as `done` when all DoD criteria are met, and end the session. Designed for spawned agents that should finish without manual intervention.

## Phase 1: Claim & Context

1. Call `pm_get(task_id)` to read the task
2. If the task is `todo` with no assignee:
   - Call `pm_grab(task_id)` to claim it (validates readiness)
   - If grab fails, stop and show the blockers
3. If the task is `in-progress` with a different assignee:
   - Warn: "This task is assigned to {assignee}. Proceed anyway?"
   - Only continue if explicitly confirmed
4. Read the parent story for broader context:
   - Call `pm_get(story_id)` to understand acceptance criteria
5. Review the task's Implementation section and DoD

## Phase 2: Execute

6. Read project documentation if touching new areas:
   - Call `pm_docs("project")` for architecture context
7. Implement the work described in the task:
   - Follow the implementation instructions
   - Write/modify the specified files
   - Run tests as described
8. Verify ALL DoD criteria are met — check off each item

## Phase 3: Complete

9. **If `--complete` flag is set:**
   - Call `pm_update(task_id, status="done")` — do NOT offer a review option
   - Check if all sibling tasks in the story are done:
     - If all done, call `pm_update(story_id, status="done")` automatically
   - Summarize what was done, files changed, tests run
   - **End the session.** Do not suggest further actions.

   **Otherwise (default):**
   - Call `pm_update(task_id, status="review")` if the task needs human review,
     OR call `pm_update(task_id, status="done")` if all DoD criteria are met
10. Check if all sibling tasks in the story are done:
    - Call `pm_active` or list tasks for the story
    - If all done, suggest: "All tasks for {story_id} are complete — update story to done?"
11. Summarize what was done, files changed, tests run
12. Suggest next action: "Check the board for more work: `/pm board`"
