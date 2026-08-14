"""Download and persist per-item Microsoft Foundry evaluation results."""

from __future__ import annotations

import argparse
import json
import re
from pathlib import Path

from azure.ai.projects import AIProjectClient
from azure.identity import DefaultAzureCredential


SAFE_ID = re.compile(r"^[A-Za-z0-9_-]+$")


def safe_segment(value: str, label: str) -> str:
    if not SAFE_ID.fullmatch(value):
        raise ValueError(f"{label} contains unsupported path characters")
    return value


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Download a Foundry evaluation run and every output item."
    )
    parser.add_argument("--project-endpoint", required=True)
    parser.add_argument("--eval-id", required=True)
    parser.add_argument("--run-id", required=True)
    parser.add_argument("--environment", default="dev")
    parser.add_argument(
        "--output-root",
        default="src/SentinelHostedAgent/.foundry/results",
    )
    return parser.parse_args()


def main() -> None:
    args = parse_args()
    eval_id = safe_segment(args.eval_id, "eval ID")
    run_id = safe_segment(args.run_id, "run ID")
    environment = safe_segment(args.environment, "environment")

    output_root = Path(args.output_root).resolve()
    output_path = output_root / environment / eval_id / f"{run_id}.json"
    output_path.parent.mkdir(parents=True, exist_ok=True)

    credential = DefaultAzureCredential()
    project_client = AIProjectClient(
        endpoint=args.project_endpoint,
        credential=credential,
    )
    openai_client = project_client.get_openai_client()

    run = openai_client.evals.runs.retrieve(run_id=run_id, eval_id=eval_id)
    items = list(
        openai_client.evals.runs.output_items.list(run_id=run_id, eval_id=eval_id)
    )
    payload = {
        "run": run.model_dump(mode="json"),
        "output_items": [item.model_dump(mode="json") for item in items],
    }

    with output_path.open("w", encoding="utf-8") as stream:
        json.dump(payload, stream, indent=2, ensure_ascii=False, default=str)
        stream.write("\n")

    status = getattr(run, "status", "unknown")
    print(f"Status: {status}")
    print(f"Output items: {len(items)}")
    print(f"Saved: {output_path}")


if __name__ == "__main__":
    main()
