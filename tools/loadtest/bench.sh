U=http://localhost:5120
T=$(curl -s -XPOST $U/api/auth/login -H 'content-type: application/json' -d '{"userName":"s.deshmukh","password":"Hospital@2026"}' | sed 's/.*"accessToken":"\([^"]*\)".*/\1/')
t() { # name path
  best=999999; size=0
  for i in 1 2 3; do
    r=$(curl -s -o /tmp/out.bin -w '%{time_total} %{size_download} %{http_code}' -H "Authorization: Bearer $T" "$U$2")
    ms=$(echo $r | awk '{printf "%d",$1*1000}'); size=$(echo $r | awk '{print $2}'); code=$(echo $r | awk '{print $3}')
    [ $ms -lt $best ] && best=$ms
  done
  printf '%-34s %6d ms %9d B  %s\n' "$1" $best $size $code
}
t dashboard /api/dashboard
t equipment-list /api/equipment?pageSize=25
t equipment-page-300 /api/equipment?page=300\&pageSize=25
t equipment-search-tag /api/equipment?q=LT-0123
t equipment-search-none /api/equipment?q=zzzzz
t equipment-filter-status /api/equipment?status=10\&pageSize=25
t equipment-by-tag /api/equipment/by-tag/LT-007000
t equipment-detail /api/equipment/7000
t equipment-history /api/equipment/7000/history
t pm-summary /api/pm/summary
t pm-tasks /api/pm/tasks?pageSize=25
t pm-tasks-overdue /api/pm/tasks?status=30\&pageSize=25
t pm-tasks-search /api/pm/tasks?q=LT-0123
t pm-tasks-search-text /api/pm/tasks?q=ventilator
t pm-schedules /api/pm/schedules
t pm-task-form /api/pm/tasks/100/form
t certificate-pdf /api/reports/pm/100/certificate.pdf
t wo-list /api/work-orders?pageSize=25
t wo-list-search /api/work-orders?q=LT-0123
t wo-summary /api/work-orders/summary
t wo-detail /api/work-orders/20000
t wo-report-pdf /api/reports/work-orders/20000/report.pdf
t locations /api/locations
t lookup-locations /api/lookups/locations
t lookup-types /api/lookups/equipment-types
t checklists /api/checklists
t users /api/users
t pilot-metrics /api/admin/pilot-metrics
t diagnostics /api/admin/diagnostics
t backups /api/admin/backups
