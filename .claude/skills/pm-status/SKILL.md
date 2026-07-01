---
name: pm-status
description: Show project status dashboard
user_invocable: true
---

# /pm-status — Status Dashboard

1. Call `pm_status` to get project overview
2. Call `pm_active` to get in-flight work
3. Present a clean dashboard showing:
   - Project name and completion percentage
   - Story/task counts by status
   - Points completed vs remaining
   - Active work items with assignees
   - Any blockers (blocked tasks)
4. Highlight items needing attention
