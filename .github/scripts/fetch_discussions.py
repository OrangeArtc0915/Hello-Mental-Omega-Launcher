#!/usr/bin/env python3
import argparse, json, os, subprocess, sys
from datetime import datetime, timezone
from html.parser import HTMLParser

class HTMLStripper(HTMLParser):
    def __init__(self):
        super().__init__()
        self.text = []
    def handle_data(self, d):
        self.text.append(d)
    def get(self):
        return ''.join(self.text)

def strip_html(s):
    if not s:
        return ''
    p = HTMLStripper()
    p.feed(s)
    return ' '.join(p.get().split())[:300]

def gql(query, variables=None):
    payload = json.dumps({'query': query, 'variables': variables or {}})
    result = subprocess.run(
        ['gh', 'api', 'graphql', '-f', f'query={query}'] +
        ([f'-f{v}={json.dumps(val)}' for v, val in (variables or {}).items()]),
        capture_output=True, text=True
    )
    if result.returncode != 0:
        print(f"GraphQL error: {result.stderr}", file=sys.stderr)
        sys.exit(1)
    data = json.loads(result.stdout)
    if 'errors' in data:
        print(f"GraphQL errors: {data['errors']}", file=sys.stderr)
        sys.exit(1)
    return data['data']

def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--owner', required=True)
    parser.add_argument('--repo', required=True)
    parser.add_argument('--out', default='WEB/data')
    args = parser.parse_args()

    os.makedirs(args.out, exist_ok=True)

    # fetch categories
    data = gql(f'''
    query {{
      repository(owner: "{args.owner}", name: "{args.repo}") {{
        discussionCategories(first: 20) {{
          nodes {{ id name slug }}
        }}
      }}
    }}
    ''')
    categories = data['repository']['discussionCategories']['nodes']

    all_discussions = []

    for cat in categories:
        has_next = True
        cursor = None
        while has_next:
            after = f', after: "{cursor}"' if cursor else ''
            q = f'''
            query($catId: ID!) {{
              repository(owner: "{args.owner}", name: "{args.repo}") {{
                discussions(first: 50, categoryId: $catId, orderBy: {{ field: CREATED_AT, direction: DESC }}{after}) {{
                  totalCount
                  pageInfo {{ hasNextPage endCursor }}
                  nodes {{
                    number
                    title
                    body
                    createdAt
                    author {{ login avatarUrl }}
                    category {{ name slug }}
                    comments {{ totalCount }}
                  }}
                }}
              }}
            }}
            '''
            d = gql(q, {'catId': cat['id']})
            disc_data = d['repository']['discussions']
            for node in disc_data['nodes']:
                all_discussions.append({
                    'number': node['number'],
                    'title': node['title'] or '',
                    'body': node['body'] or '',
                    'body_text': strip_html(node['body']),
                    'created_at': node['createdAt'],
                    'author': {
                        'login': (node.get('author') or {}).get('login', 'unknown'),
                        'avatar_url': (node.get('author') or {}).get('avatarUrl', ''),
                    },
                    'category': (node.get('category') or {}).get('name', ''),
                    'comments_count': (node.get('comments') or {}).get('totalCount', 0),
                })
            has_next = disc_data['pageInfo']['hasNextPage']
            cursor = disc_data['pageInfo']['endCursor']

    output = {
        'updated_at': datetime.now(timezone.utc).strftime('%Y-%m-%dT%H:%M:%SZ'),
        'categories': [{'id': c['id'], 'name': c['name'], 'slug': c['slug']} for c in categories],
        'discussions': all_discussions,
    }

    path = os.path.join(args.out, 'discussions.json')
    with open(path, 'w', encoding='utf-8') as f:
        json.dump(output, f, ensure_ascii=False)

    print(f'Wrote {len(all_discussions)} discussions from {len(categories)} categories to {path}')

if __name__ == '__main__':
    main()
