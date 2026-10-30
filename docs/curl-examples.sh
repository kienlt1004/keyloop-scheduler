#!/usr/bin/env bash
# cURL walkthrough against the seeded data. Usage:  ./docs/curl-examples.sh [base-url]
# Start the stack first:  docker compose up --build
set -u
BASE="${1:-http://localhost:8080}"

LONDON=11111111-1111-1111-1111-111111111111
MANCHESTER=22222222-2222-2222-2222-222222222222
MOT=cccccccc-0000-0000-0000-000000000002
JANE=dddddddd-0000-0000-0000-000000000001
OMAR=dddddddd-0000-0000-0000-000000000002
JANE_GOLF=eeeeeeee-0000-0000-0000-000000000001
JANE_TESLA=eeeeeeee-0000-0000-0000-000000000002
OMAR_DEFENDER=eeeeeeee-0000-0000-0000-000000000003

# The API only accepts FUTURE, 15-minute-aligned times inside opening hours (Mon-Sat). Use next Tuesday.
DAY=$(date -u -d "next tuesday" +%F)
echo "Using date: $DAY"

post() { # customer vehicle dealership startTime [idempotency-key]
  curl -sS -i -X POST "$BASE/api/v1/appointments" \
    -H 'Content-Type: application/json' \
    ${5:+-H "Idempotency-Key: $5"} \
    -d "{\"customerId\":\"$1\",\"vehicleId\":\"$2\",\"dealershipId\":\"$3\",\"serviceTypeId\":\"$MOT\",\"startTime\":\"$4\"}" \
    | sed -n '1p;/^[Ll]ocation/p;/^[Ii]dempotent/p;/^{/p'
  echo; echo
}

echo "### 1. Availability for an MOT at the London dealership"
curl -sS "$BASE/api/v1/dealerships/$LONDON/availability?serviceTypeId=$MOT&date=$DAY" | head -c 400; echo; echo

echo "### 2. Book Jane's Golf at Manchester (1 bay, 1 technician) at 10:00 -> 201"
post $JANE $JANE_GOLF $MANCHESTER "${DAY}T10:00:00Z" "demo-key-1"

echo "### 3. Retry with the SAME Idempotency-Key -> 200 + Idempotent-Replayed (no duplicate)"
post $JANE $JANE_GOLF $MANCHESTER "${DAY}T10:00:00Z" "demo-key-1"

echo "### 4. Omar tries to overlap (10:30) at Manchester -> 409 no-capacity"
post $OMAR $OMAR_DEFENDER $MANCHESTER "${DAY}T10:30:00Z"

echo "### 5. Omar books back-to-back (11:00) -> 201 (half-open intervals)"
post $OMAR $OMAR_DEFENDER $MANCHESTER "${DAY}T11:00:00Z"

echo "### 6. Outside opening hours (06:00) -> 422"
post $JANE $JANE_TESLA $LONDON "${DAY}T06:00:00Z"

echo "### 7. Vehicle that is not the customer's -> 422"
post $JANE $OMAR_DEFENDER $LONDON "${DAY}T09:00:00Z"

echo "### 8. List Jane's appointments"
curl -sS "$BASE/api/v1/appointments?customerId=$JANE" | head -c 600; echo; echo

echo "### 9. Cancel (replace <id> with an appointment id from above)"
echo "curl -i -X DELETE $BASE/api/v1/appointments/<id>"
