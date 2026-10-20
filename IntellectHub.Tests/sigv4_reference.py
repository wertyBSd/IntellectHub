"""Reference SigV4 signatures, produced by botocore (the AWS SDK's own signer).

The C# client in IntellectHub.Client signs Cloudflare R2 requests with its own
implementation of Signature Version 4. This script generates the expected
values for StorageSigV4Tests, so that test can check the implementation
against the AWS SDK rather than against itself.

Run it after changing what the client signs, and update the constants in
StorageSigV4Tests.cs with what it prints.

    python IntellectHub.Tests/sigv4_reference.py
"""

import json
import sys
from datetime import datetime, timezone

import botocore.auth
from botocore.auth import SigV4Auth
from botocore.awsrequest import AWSRequest
from botocore.credentials import Credentials

ACCESS_KEY = "AKIAIOSFODNN7EXAMPLE"
SECRET_KEY = "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY"
REGION = "auto"
SERVICE = "s3"

FIXED = datetime(2026, 1, 15, 12, 34, 56, tzinfo=timezone.utc)
STAMP = FIXED.strftime("%Y%m%dT%H%M%SZ")

# botocore stamps the request with the current time; the clock is pinned so the
# expected values stay the same from one run to the next.
botocore.auth.get_current_datetime = lambda: FIXED


def sign(method, url, body=b""):
    """Signs a request the way the AWS SDK does, and reports what it covered."""
    credentials = Credentials(ACCESS_KEY, SECRET_KEY)
    auth = SigV4Auth(credentials, SERVICE, REGION)

    request = AWSRequest(method=method, url=url, data=body)
    request.headers["host"] = request.url.split("/")[2]
    request.headers["x-amz-content-sha256"] = auth.payload(request)

    signed = auth.headers_to_sign(request)
    auth.add_auth(request)

    return {
        "method": method,
        "url": url,
        "authorization": request.headers["Authorization"],
        "signedHeaders": auth.signed_headers(signed),
        "payloadHash": request.headers["x-amz-content-sha256"],
    }


CASES = [
    ("get", "GET", "https://bucket.r2.cloudflarestorage.com/folder/file.txt", b""),
    ("put", "PUT", "https://bucket.r2.cloudflarestorage.com/folder/file.txt", b"hello from the client"),
    ("delete", "DELETE", "https://bucket.r2.cloudflarestorage.com/folder/file.txt", b""),
    ("query", "GET", "https://bucket.r2.cloudflarestorage.com/folder/file.txt?versionId=42", b""),
    # The SDK is given the object name as it is and encodes the path itself, so
    # the canonical path is encoded exactly once.
    ("space", "GET", "https://bucket.r2.cloudflarestorage.com/folder/a b.txt", b""),
    ("nested", "GET", "https://bucket.r2.cloudflarestorage.com/a/b/c/d e/f.txt", b""),
]

results = {}
for name, method, url, body in CASES:
    results[name] = sign(method, url, body)

print(json.dumps(results, indent=2))
sys.stdout.write("\n")
