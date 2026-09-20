set session_replication_role = replica;
\timing on
create temp table base_eq as select row_number() over (order by id) rn, equipment_type_id, location_id, status from equipment;
create temp table base_sch as select row_number() over (order by id) rn, checklist_template_id, frequency, interval_days, grace_days from pm_schedule;
select count(*) as base_eq from base_eq \gset
select count(*) as base_sch from base_sch \gset

insert into equipment (asset_tag, serial_number, equipment_type_id, location_id, manufacturer, model, status)
select 'LT-'||lpad(n::text,6,'0'), 'SN'||n*7919, b.equipment_type_id, b.location_id, 'Maker'||(n%40), 'Model'||(n%120), b.status
from generate_series(1, 15000-(select count(*) from equipment)) n
join base_eq b on b.rn = (n % :base_eq)+1;

insert into pm_schedule (equipment_id, checklist_template_id, frequency, interval_days, anchor_date, grace_days)
select e.id, b.checklist_template_id, b.frequency, b.interval_days, current_date - 365, b.grace_days
from equipment e join base_sch b on b.rn = (e.id % :base_sch)+1
where e.asset_tag like 'LT-%';

-- a year of history plus the next quarter, every 30/90/180 days by schedule
insert into pm_task (pm_schedule_id, equipment_id, due_date, status, completed_at_utc, completed_by_user_id, checklist_template_version_id)
select s.id, s.equipment_id, d::date,
  case when d::date < current_date - 20 then 40 when d::date < current_date and s.id % 25 = 0 then 30 else 10 end,
  case when d::date < current_date - 20 then d + interval '1 hour' end,
  case when d::date < current_date - 20 then 1 end,
  case when d::date < current_date - 20 then 1 end
from pm_schedule s
cross join lateral generate_series(current_date - 365 + (s.id % 30), current_date + 90,
   ((array[30,90,180])[1 + s.id % 3] || ' days')::interval) d
where s.equipment_id in (select id from equipment where asset_tag like 'LT-%');

insert into pm_completion (pm_task_id, checklist_template_version_id, answers, signed_by_name, completed_by_user_id, completed_at_utc)
select t.id, 1, (select answers from pm_completion order by id limit 1), 'Load Test', 1, t.completed_at_utc
from pm_task t where t.status = 40 and not exists (select 1 from pm_completion c where c.pm_task_id = t.id);

insert into work_order (number, equipment_id, status, priority, fault_description, reported_by_user_id, reported_at_utc,
   resolution_notes, resolved_by_user_id, resolved_at_utc, closed_at_utc, out_of_service_at_utc, back_in_service_at_utc)
select 'WO-LT-'||lpad(n::text,6,'0'), 1 + (n * 7) % 15000 ,
  case when n % 65 = 0 then 10 + 10*(n%4) when n % 30 = 0 then 70 else 60 end, 10*(1+n%4),
  'Load test fault '||n||' - alarm and no display, checked by engineer', 1,
  now() - (n % 1095 || ' days')::interval,
  case when n % 65 <> 0 and n % 30 <> 0 then 'Replaced part and tested' end,
  case when n % 65 <> 0 and n % 30 <> 0 then 1 end,
  case when n % 65 <> 0 and n % 30 <> 0 then now() - (n % 1095 || ' days')::interval + interval '5 hours' end,
  case when n % 65 <> 0 and n % 30 <> 0 then now() - (n % 1095 || ' days')::interval + interval '6 hours' end,
  now() - (n % 1095 || ' days')::interval + interval '1 hour',
  case when n % 65 <> 0 and n % 30 <> 0 then now() - (n % 1095 || ' days')::interval + interval '5 hours' end
from generate_series(1, 50000) n;
-- (work_order ids can't be guaranteed to line up with 1..15000: use a modular pick over real ids instead)
set session_replication_role = origin;
analyze;
select (select count(*) from equipment) eq,(select count(*) from pm_schedule) sch,(select count(*) from pm_task) tasks,(select count(*) from pm_completion) comp,(select count(*) from work_order) wo,pg_size_pretty(pg_database_size('hospitalpm'));
