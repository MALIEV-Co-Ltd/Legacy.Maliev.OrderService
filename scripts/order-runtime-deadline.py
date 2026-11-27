"""Fail-closed unauthenticated run-clock admission for Order runtime jobs."""
import datetime as dt
import email.utils
import json
import os
import re
import sys
import urllib.request

ROOT = 'https://api.github.com/repos/MALIEV-Co-Ltd/Legacy.Maliev.OrderService/actions/runs/'
LIMIT = 262144


def fetch(url):
    request = urllib.request.Request(url, headers={'Cache-Control': 'no-cache',
        'Accept': 'application/vnd.github+json', 'X-GitHub-Api-Version': '2022-11-28',
        'User-Agent': 'order-runtime-admission'})
    with urllib.request.urlopen(request, timeout=10) as response:
        if response.status != 200:
            raise ValueError('metadata status')
        date = email.utils.parsedate_to_datetime(response.headers['Date']).astimezone(dt.timezone.utc)
        now = dt.datetime.now(dt.timezone.utc)
        if abs((now - date).total_seconds()) > 30:
            raise ValueError('stale or skewed server clock')
        body = response.read(LIMIT + 1)
        if len(body) > LIMIT:
            raise ValueError('oversized metadata')
    return json.loads(body), date


def evaluate(phase, run_id, attempt, job_name, runner_name, run, jobs, now):
    if phase not in ('validate',):
        raise ValueError('unknown phase')
    if not re.fullmatch(r'[1-9][0-9]*', run_id) or not re.fullmatch(r'[1-9][0-9]*', attempt):
        raise ValueError('invalid run identity')
    if job_name != 'validate' or not runner_name:
        raise ValueError('unexpected job identity')
    if run['id'] != int(run_id) or run['run_attempt'] != int(attempt):
        raise ValueError('run attempt mismatch')
    rows = jobs['jobs']
    if jobs['total_count'] != len(rows) or len(rows) > 100:
        raise ValueError('incomplete job inventory')
    matches = [x for x in rows if x['name'] == job_name and x['runner_name'] == runner_name
               and x['run_id'] == int(run_id) and x['run_attempt'] == int(attempt)
               and x['status'] == 'in_progress']
    if len(matches) != 1:
        raise ValueError('current job not uniquely identified')
    parse = lambda value: dt.datetime.fromisoformat(value.replace('Z', '+00:00'))
    run_start, job_start = parse(run['run_started_at']), parse(matches[0]['started_at'])
    if run_start.tzinfo is None or job_start.tzinfo is None or not run_start <= job_start <= now:
        raise ValueError('uncertain run/job timing')
    remaining = (run_start + dt.timedelta(minutes=45) - now).total_seconds()
    # Covers phase, subsequent bounded steps, upload and two minutes timing/post slack.
    required = {'validate': 40 * 60}[phase]
    if not 0 < remaining <= 45 * 60 or remaining < required:
        raise ValueError('insufficient reserve')
    return int(remaining)


def main():
    if len(sys.argv) != 2:
        raise ValueError('missing phase')
    run_id, attempt = os.environ['GITHUB_RUN_ID'], os.environ['GITHUB_RUN_ATTEMPT']
    if not re.fullmatch(r'[1-9][0-9]*', run_id) or not re.fullmatch(r'[1-9][0-9]*', attempt):
        raise ValueError('invalid run identity')
    if os.environ['GITHUB_REPOSITORY'] != 'MALIEV-Co-Ltd/Legacy.Maliev.OrderService':
        raise ValueError('unexpected repository')
    prefix = f'{ROOT}{run_id}/attempts/{attempt}'
    run, _ = fetch(prefix)
    jobs, now = fetch(prefix + '/jobs?per_page=100')
    remaining = evaluate(sys.argv[1], run_id, attempt, os.environ['GITHUB_JOB'],
                         os.environ['RUNNER_NAME'], run, jobs, now)
    print(f'Admitted {sys.argv[1]}: conservative remaining={remaining}s')


if __name__ == '__main__':
    try:
        main()
    except Exception as error:
        print(f'Order runtime time admission refused: {type(error).__name__}', file=sys.stderr)
        sys.exit(1)
