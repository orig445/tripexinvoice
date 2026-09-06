#!/usr/bin/env bash
# ==============================================================================
# Milo fine-tune — OCI Generative AI setup
#
# Run this yourself via OCI CLI (locally, if installed) or OCI Cloud Shell
# (no install needed — console.oraclecloud.com -> Cloud Shell icon, top right).
# This is NOT run automatically — it provisions BILLED resources (Dedicated AI
# Clusters bill per hour whether or not you're actively using them), so each
# step below is meant to be reviewed and run one at a time, not blindly piped.
#
# Cost note: a fine-tuning cluster only runs for the duration of the training
# job, but a HOSTING cluster (step 8) bills continuously from the moment it's
# created until you delete it. Do not create the hosting cluster until you've
# validated the pilot model's quality (step 7b) and are ready to actually serve
# it — and see the teardown section at the bottom for how to stop the bleeding
# if you created one you don't need anymore.
#
# Honesty note: I don't have access to a live OCI account from here, so none
# of the commands below have been run against the real API — they're built
# from OCI's current published CLI docs, not verified end-to-end. Run each
# `--help` before the real command (e.g. `oci generative-ai model create
# --help`) and sanity-check field names against it — OCI's CLI surface for
# Generative AI is relatively new and shifts between releases.
# ==============================================================================

set -euo pipefail

# ---- fill these in ----
TENANCY_OCID="ocid1.tenancy.oc1..aaaaaaaacjrpn6rop45tponbhlgnwmlkmwulwdykj64sj274yyfw2qlwb7ra"
# No dedicated compartment given — using the tenancy root compartment (its OCID
# is always identical to the tenancy OCID in OCI). Fine for a small setup; if
# you later want fine-tuning spend broken out separately in Cost Analysis, run
# step 0 to create a dedicated compartment and swap this value.
COMPARTMENT_OCID="$TENANCY_OCID"
REGION="us-chicago-1"   # matches the region already used by OracleAiService.cs
BUCKET_NAME="milo-finetune-data"

DATA_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/out" && pwd)"
PILOT_FILE="$DATA_DIR/milo_finetune_pilot.jsonl"
FULL_FILE="$DATA_DIR/milo_finetune_full.jsonl"

# ==============================================================================
# STEP 0 (optional) — create a dedicated compartment for this experiment,
# instead of reusing the production compartment. Recommended so fine-tuning
# spend shows up separately in Cost Analysis and you can tear it all down by
# deleting one compartment. Skip if you'd rather reuse an existing one.
# ==============================================================================
# oci iam compartment create \
#   --compartment-id "$TENANCY_OCID" \
#   --name "milo-finetune" \
#   --description "Milo chatbot fine-tuning experiment (Glassix history)"
# # -> copy the returned "id" into COMPARTMENT_OCID above

# ==============================================================================
# STEP 1 — create the Object Storage bucket and upload both JSONL files
# ==============================================================================
oci os bucket create \
  --compartment-id "$COMPARTMENT_OCID" \
  --name "$BUCKET_NAME" \
  --region "$REGION"

oci os object put \
  --bucket-name "$BUCKET_NAME" --region "$REGION" \
  --file "$PILOT_FILE" --name "milo_finetune_pilot.jsonl"

oci os object put \
  --bucket-name "$BUCKET_NAME" --region "$REGION" \
  --file "$FULL_FILE" --name "milo_finetune_full.jsonl"

# ==============================================================================
# STEP 2 — discover which base models are actually fine-tunable in this region
# right now (don't trust a hardcoded model OCID — this list changes over time).
# Look for CAPABILITY "FINE_TUNE" and note the OCID of a Cohere Command or
# Meta Llama model (NOT the Gemini one Milo currently uses — Gemini isn't
# fine-tunable via OCI).
# ==============================================================================
oci generative-ai model list \
  --compartment-id "$COMPARTMENT_OCID" --region "$REGION" \
  --query "data[?contains(capabilities,'FINE_TUNE')].{name:\"display-name\",id:id,vendor:vendor}" \
  --output table

# -> pick one and set:
BASE_MODEL_OCID="<FILL IN from the table above>"

# ==============================================================================
# STEP 3 — fine-tuning cluster (pilot). This is what actually costs money to
# run — the console/CLI will show you the exact unit-hour commitment and ask
# you to accept it before this creates anything.
#
# I have NOT run or verified this command against a live OCI account (no
# access to one from here) — the flag names below match OCI's current
# documented shape, but treat this as a well-researched draft, not a tested
# script. Run `oci generative-ai dedicated-ai-cluster create --help` first and
# diff it against this before executing — the model you picked in step 2 also
# has a required "unit-shape" value shown in its details (`oci generative-ai
# model get --model-id "$BASE_MODEL_OCID" --region "$REGION"`), which the
# create command below needs and I've left as a placeholder since it's
# specific to whichever base model you actually pick.
# ==============================================================================
oci generative-ai dedicated-ai-cluster create \
  --compartment-id "$COMPARTMENT_OCID" --region "$REGION" \
  --type "FINE_TUNING" \
  --unit-shape "<FILL IN — from `oci generative-ai model get` on your chosen base model>" \
  --display-name "milo-pilot-finetune-cluster"
# -> note the returned cluster "id" as FT_CLUSTER_OCID

# ==============================================================================
# STEP 4 — kick off the pilot fine-tune job on the small 3,000-pair sample
# ==============================================================================
FT_CLUSTER_OCID="<FILL IN from step 3>"

oci generative-ai model create \
  --compartment-id "$COMPARTMENT_OCID" --region "$REGION" \
  --display-name "milo-pilot-v1" \
  --base-model-id "$BASE_MODEL_OCID" \
  --fine-tune-details "{
    \"dedicatedAiClusterId\": \"$FT_CLUSTER_OCID\",
    \"trainingDataset\": {
      \"datasetType\": \"OBJECT_STORAGE\",
      \"bucket\": \"$BUCKET_NAME\",
      \"namespace\": \"$(oci os ns get --query 'data' --raw-output)\",
      \"object\": \"milo_finetune_pilot.jsonl\"
    }
  }"
# -> takes a while; poll with:
#    oci generative-ai model get --model-id <returned model id> --region "$REGION" --query 'data."lifecycle-state"'

# ==============================================================================
# STEP 5 — hosting cluster + endpoint, ONLY once you're ready to actually query
# the pilot model (this is the one that bills continuously — see cost note up top)
# ==============================================================================
# oci generative-ai dedicated-ai-cluster create \
#   --compartment-id "$COMPARTMENT_OCID" --region "$REGION" \
#   --type "HOSTING" \
#   --display-name "milo-pilot-hosting-cluster"
# -> note the returned cluster "id" as HOST_CLUSTER_OCID
#
# oci generative-ai endpoint create \
#   --compartment-id "$COMPARTMENT_OCID" --region "$REGION" \
#   --dedicated-ai-cluster-id "<HOST_CLUSTER_OCID>" \
#   --model-id "<pilot model id from step 4>" \
#   --display-name "milo-pilot-endpoint"
# -> note the returned "content-endpoint" URL — this is what OracleAiService's
#    Oracle:CustomModelEndpoint config value will point to (see Phase 3 of the plan)

# ==============================================================================
# STEP 6 — quality check BEFORE scaling to the full 17,075-pair dataset:
# run a batch of real historical customer questions (held out — not in the
# training set) through the pilot endpoint and compare against:
#   (a) the REAL historical agent answer already on file for that ticket, and
#   (b) what current production Milo (Gemini) says for the same question.
# Pay particular attention to Hebrew fluency — this is the actual risk being
# tested by doing a cheap pilot before the full run.
# ==============================================================================
# (I can write this eval script once you have a live pilot endpoint to call.)

# ==============================================================================
# TEARDOWN — delete anything you're not actively using. Dedicated AI Clusters
# bill per hour whether or not a request ever hits them.
# ==============================================================================
# oci generative-ai dedicated-ai-cluster delete --dedicated-ai-cluster-id "<id>" --region "$REGION" --force
