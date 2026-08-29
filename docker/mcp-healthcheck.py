"""Container healthcheck for the Alpaca MCP server.

The streamable-http endpoint answers a bare GET with an HTTP error, because the
request has no MCP session. Any HTTP status therefore proves that the server
listens and serves. Only a connection failure or a timeout is unhealthy.
"""

import os
import sys
import urllib.error
import urllib.request

url = os.environ.get("MCP_HEALTHCHECK_URL", "http://127.0.0.1:8000/mcp")

try:
    urllib.request.urlopen(url, timeout=4)
except urllib.error.HTTPError:
    pass
except Exception as error:  # connection refused, DNS, timeout
    print(f"unhealthy: {error}", file=sys.stderr)
    sys.exit(1)
