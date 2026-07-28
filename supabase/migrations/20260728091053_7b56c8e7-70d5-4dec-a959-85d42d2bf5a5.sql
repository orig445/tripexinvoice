
create extension if not exists pg_cron with schema pg_catalog;
create extension if not exists pg_net with schema extensions;

-- Unschedule any prior job with the same name (safe if not present)
do $$
declare j record;
begin
  for j in select jobid from cron.job where jobname = 'outlook-support-agent-every-2min' loop
    perform cron.unschedule(j.jobid);
  end loop;
end$$;

select cron.schedule(
  'outlook-support-agent-every-2min',
  '*/2 * * * *',
  $$
  select net.http_post(
    url := 'https://osuyokvyhiyvyhjrbcxm.supabase.co/functions/v1/outlook-support-agent',
    headers := jsonb_build_object('Content-Type','application/json'),
    body := '{}'::jsonb
  ) as request_id;
  $$
);
