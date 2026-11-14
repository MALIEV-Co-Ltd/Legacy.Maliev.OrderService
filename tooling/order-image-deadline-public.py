"""Fail-closed unauthenticated run-clock admission for Order image jobs."""
import datetime as dt
import email.utils
import json
import os
import re
import secrets
import sys
import urllib.request
from urllib.error import HTTPError

ROOT = 'https://api.github.com/repos/MALIEV-Co-Ltd/Legacy.Maliev.OrderService/actions/runs/'
LIMIT = 262144


def fetch(url):
    separator = '&' if '?' in url else '?'
    url = f'{url}{separator}codex_nonce={secrets.token_hex(16)}'
    request = urllib.request.Request(url, headers={'Cache-Control': 'no-cache',
        'Accept': 'application/vnd.github+json', 'X-GitHub-Api-Version': '2022-11-28',
        'User-Agent': 'order-image-admission'})
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
    if phase not in ('build', 'startup'):
        raise ValueError('unknown phase')
    if not re.fullmatch(r'[1-9][0-9]*', run_id) or not re.fullmatch(r'[1-9][0-9]*', attempt):
        raise ValueError('invalid run identity')
    if job_name != 'image-build' or not runner_name:
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
    remaining = (run_start + dt.timedelta(minutes=50) - now).total_seconds()
    # Covers phase, subsequent bounded steps, upload and two minutes timing/post slack.
    required = {'build': 43 * 60, 'startup': 11 * 60}[phase]
    if not 0 < remaining <= 50 * 60 or remaining < required:
        raise ValueError('insufficient reserve')
    return int(remaining)


def fetch_labeled(url, label):
    if label not in ('run', 'jobs'):
        raise ValueError('unknown metadata endpoint')
    try:
        return fetch(url)
    except ValueError as error:
        if str(error) == 'stale or skewed server clock':
            print(f'Order metadata endpoint={label} stale or skewed server clock', file=sys.stderr)
        raise
    except HTTPError as error:
        code = error.code if type(error.code) is int and 100 <= error.code <= 599 else 'unknown'
        print(f'Order metadata endpoint={label} HTTP status={code}', file=sys.stderr)
        raise


def main():
    if len(sys.argv) != 2:
        raise ValueError('missing phase')
    run_id, attempt = os.environ['GITHUB_RUN_ID'], os.environ['GITHUB_RUN_ATTEMPT']
    if not re.fullmatch(r'[1-9][0-9]*', run_id) or not re.fullmatch(r'[1-9][0-9]*', attempt):
        raise ValueError('invalid run identity')
    if os.environ['GITHUB_REPOSITORY'] != 'MALIEV-Co-Ltd/Legacy.Maliev.OrderService':
        raise ValueError('unexpected repository')
    prefix = f'{ROOT}{run_id}/attempts/{attempt}'
    run, _ = fetch_labeled(prefix, 'run')
    jobs, now = fetch_labeled(prefix + '/jobs?per_page=100', 'jobs')
    remaining = evaluate(sys.argv[1], run_id, attempt, os.environ['GITHUB_JOB'],
                         os.environ['RUNNER_NAME'], run, jobs, now)
    print(f'Admitted {sys.argv[1]}: conservative remaining={remaining}s')


if __name__ == '__main__':
    try:
        main()
    except Exception as error:
        print(f'Order image time admission refused: {type(error).__name__}', file=sys.stderr)
        sys.exit(1)
