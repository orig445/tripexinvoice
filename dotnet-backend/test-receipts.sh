#!/bin/bash
# =============================================================================
# TripEx Invoice OCR - Bulk Receipt Validation Script
# =============================================================================
# Usage:
#   ./test-receipts.sh <receipts_folder> [api_url] [api_key] [country]
#
# Example:
#   ./test-receipts.sh ./test-receipts https://localhost:5001 my-api-key IL
#
# Requirements: curl, jq, base64
# =============================================================================

set -u

RECEIPTS_DIR="${1:-./test-receipts}"
API_URL="${2:-https://localhost:5001}"
API_KEY="${3:-${TRIPEX_API_KEY:-YOUR_STATIC_API_KEY_HERE}}"
COUNTRY="${4:-IL}"

ENDPOINT="${API_URL}/api/invoice/analyze"
RESULTS_FILE="/tmp/tripex-test-results-$(date +%Y%m%d-%H%M%S).json"

# Colors
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
CYAN='\033[0;36m'
BOLD='\033[1m'
NC='\033[0m'

# Check deps
for cmd in curl jq base64; do
  if ! command -v "$cmd" >/dev/null 2>&1; then
    echo -e "${RED}Missing dependency: $cmd${NC}"; exit 1;
  fi
done

if [ ! -d "$RECEIPTS_DIR" ]; then
  echo -e "${RED}Folder not found: $RECEIPTS_DIR${NC}"
  echo "Create it and drop receipt images (.jpg/.jpeg/.png/.pdf) inside."
  exit 1
fi

shopt -s nullglob nocaseglob
FILES=("$RECEIPTS_DIR"/*.{jpg,jpeg,png,pdf})
shopt -u nocaseglob
if [ ${#FILES[@]} -eq 0 ]; then
  echo -e "${RED}No receipt files in $RECEIPTS_DIR${NC}"; exit 1;
fi

echo -e "${BOLD}${CYAN}╔══════════════════════════════════════════════════════════════════╗${NC}"
echo -e "${BOLD}${CYAN}║         TripEx Invoice OCR - Bulk Validation Test                ║${NC}"
echo -e "${BOLD}${CYAN}╚══════════════════════════════════════════════════════════════════╝${NC}"
echo -e "Endpoint : ${API_URL}"
echo -e "Country  : ${COUNTRY}"
echo -e "Files    : ${#FILES[@]}"
echo -e "Log file : ${RESULTS_FILE}"
echo ""

# Header
printf "${BOLD}%-30s | %-8s | %-12s | %-6s | %-12s | %-15s | %s${NC}\n" \
  "FILE" "STATUS" "TOTAL" "CCY" "DATE" "INVOICE #" "VENDOR"
printf '%.0s-' {1..130}; echo ""

PASS=0; FAIL=0; ZERO=0
echo "[" > "$RESULTS_FILE"
FIRST=1

for f in "${FILES[@]}"; do
  fname=$(basename "$f")
  short_name="${fname:0:28}"

  # base64 encode
  b64=$(base64 -w 0 "$f" 2>/dev/null || base64 "$f" | tr -d '\n')

  # Build payload
  payload=$(jq -n --arg img "$b64" --arg c "$COUNTRY" \
    '{image_base64: $img, country: $c}')

  # Call API
  resp=$(curl -sk -X POST "$ENDPOINT" \
    -H "Content-Type: application/json" \
    -H "X-Api-Key: ${API_KEY}" \
    --max-time 90 \
    -d "$payload" 2>/dev/null)

  if [ -z "$resp" ]; then
    printf "${RED}%-30s | %-8s | %s${NC}\n" "$short_name" "ERROR" "no response from API"
    FAIL=$((FAIL+1)); continue
  fi

  # Extract fields (try both algotext + mindee paths)
  success=$(echo "$resp" | jq -r '.success // .Success // false' 2>/dev/null)
  total=$(echo "$resp" | jq -r '.fields.total // .Fields.total // .document.inference.prediction.total_amount.value // ""' 2>/dev/null)
  ccy=$(echo "$resp" | jq -r '.fields.currency // .Fields.currency // ""' 2>/dev/null)
  date=$(echo "$resp" | jq -r '.fields.invoiceDate // .fields.invoice_date // .Fields.invoiceDate // ""' 2>/dev/null)
  invnum=$(echo "$resp" | jq -r '.fields.invoiceNumber // .fields.invoice_number // .Fields.invoiceNumber // ""' 2>/dev/null)
  vendor=$(echo "$resp" | jq -r '.fields.vendorName // .fields.vendor_name // .Fields.vendorName // ""' 2>/dev/null)
  err=$(echo "$resp" | jq -r '.error // .Error // ""' 2>/dev/null)

  # Validate
  total_num=$(echo "$total" | tr -d ',' | tr -d ' ')
  is_zero=0
  if [ -z "$total_num" ] || [ "$total_num" = "null" ] || [ "$total_num" = "0" ] || [ "$total_num" = "0.00" ]; then
    is_zero=1
  fi

  if [ "$success" = "true" ] && [ "$is_zero" = "0" ]; then
    status="${GREEN}OK${NC}"
    PASS=$((PASS+1))
  elif [ "$is_zero" = "1" ]; then
    status="${RED}ZERO!${NC}"
    ZERO=$((ZERO+1))
    FAIL=$((FAIL+1))
  else
    status="${RED}FAIL${NC}"
    FAIL=$((FAIL+1))
  fi

  printf "%-30s | ${status}%*s | %-12s | %-6s | %-12s | %-15s | %s\n" \
    "$short_name" $((8 - 4)) "" \
    "${total:0:12}" "${ccy:0:6}" "${date:0:12}" "${invnum:0:15}" "${vendor:0:30}"

  if [ -n "$err" ] && [ "$err" != "null" ] && [ "$err" != "" ]; then
    echo -e "    ${YELLOW}↳ $err${NC}"
  fi

  # Append to results json
  if [ $FIRST -eq 0 ]; then echo "," >> "$RESULTS_FILE"; fi
  FIRST=0
  jq -n --arg file "$fname" --argjson resp "$resp" \
    '{file: $file, response: $resp}' >> "$RESULTS_FILE"
done

echo "]" >> "$RESULTS_FILE"

echo ""
printf '%.0s=' {1..130}; echo ""
echo -e "${BOLD}SUMMARY${NC}"
echo -e "  Total tested : ${#FILES[@]}"
echo -e "  ${GREEN}Passed       : ${PASS}${NC}"
echo -e "  ${RED}Failed       : ${FAIL}${NC}"
echo -e "  ${RED}Zero amount  : ${ZERO}  ← MUST be 0 for production!${NC}"
echo -e "  Full log     : ${RESULTS_FILE}"
echo ""

if [ "$ZERO" -gt 0 ] || [ "$FAIL" -gt 0 ]; then
  exit 1
fi
exit 0
