---
name: pm-plan
description: Sprint planning workflow
user_invocable: true
---

# /pm-plan — Sprint Planning

Run the full sprint planning workflow:

1. Call `pm_status` for current state
2. Call `pm_audit` to check for drift issues
3. Call `pm_active` for in-flight work
4. Call `pm_burndown` for velocity data
5. Present findings and guide prioritization:
   - Show audit issues that need resolution
   - List backlog stories by priority
   - Recommend stories to scope next
6. For each selected story:
   - Call `pm_scope(story_id)` to get decomposition context
   - Propose task breakdown
   - Call `pm_estimate(id)` for each task
7. Create tasks on user approval
8. Summarize the sprint plan
