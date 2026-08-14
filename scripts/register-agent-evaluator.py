#!/usr/bin/env python3
"""Register the initial JWT Sentinel rubric evaluator in Microsoft Foundry."""

from __future__ import annotations

import argparse
import json
from pathlib import Path
from typing import Any

from azure.ai.projects import AIProjectClient
from azure.ai.projects.models import EvaluatorCategory, EvaluatorDefinitionType
from azure.core.exceptions import ResourceNotFoundError
from azure.identity import AzureCliCredential


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description=(
            "Validate a rubric locally and, with --apply, create its initial "
            "custom-evaluator version in a Foundry project. Existing evaluators "
            "are never overwritten by this script."
        )
    )
    parser.add_argument("--project-endpoint", required=True)
    parser.add_argument("--name", required=True)
    parser.add_argument("--rubric", required=True, type=Path)
    parser.add_argument("--display-name", default="JWT Sentinel Security Parity")
    parser.add_argument(
        "--description",
        default=(
            "Security, evidence, grounding, confidentiality, and response-quality "
            "parity rubric for the JWT Sentinel Hosted Agent."
        ),
    )
    parser.add_argument("--pass-threshold", type=float, default=0.8)
    parser.add_argument(
        "--apply",
        action="store_true",
        help="Create the initial evaluator version. Without this flag, validate only.",
    )
    return parser.parse_args()


def load_dimensions(path: Path) -> list[dict[str, Any]]:
    dimensions = json.loads(path.read_text(encoding="utf-8"))
    if not isinstance(dimensions, list) or not dimensions:
        raise ValueError("Rubric must be a nonempty JSON array.")

    seen: set[str] = set()
    for dimension in dimensions:
        if not isinstance(dimension, dict):
            raise ValueError("Every rubric dimension must be a JSON object.")
        dimension_id = dimension.get("id")
        description = dimension.get("description")
        weight = dimension.get("weight")
        if not isinstance(dimension_id, str) or not dimension_id:
            raise ValueError("Every rubric dimension requires a nonempty string id.")
        if dimension_id in seen:
            raise ValueError(f"Duplicate rubric dimension id: {dimension_id}")
        if not isinstance(description, str) or not description:
            raise ValueError(f"Dimension {dimension_id} requires a description.")
        if not isinstance(weight, int) or not 1 <= weight <= 10:
            raise ValueError(f"Dimension {dimension_id} weight must be an integer from 1 to 10.")
        if "always_applicable" in dimension and not isinstance(
            dimension["always_applicable"], bool
        ):
            raise ValueError(f"Dimension {dimension_id} always_applicable must be boolean.")
        seen.add(dimension_id)

    return dimensions


def main() -> int:
    args = parse_args()
    if not 0.0 <= args.pass_threshold <= 1.0:
        raise ValueError("--pass-threshold must be between 0.0 and 1.0.")

    dimensions = load_dimensions(args.rubric.resolve())
    print(f"Validated {len(dimensions)} rubric dimensions for {args.name}.")
    if not args.apply:
        print("Dry run only; no Foundry evaluator was created.")
        return 0

    credential = AzureCliCredential()
    with AIProjectClient(
        endpoint=args.project_endpoint,
        credential=credential,
    ) as project_client:
        try:
            existing = list(
                project_client.beta.evaluators.list_versions(
                    args.name,
                    type="custom",
                    limit=100,
                )
            )
        except ResourceNotFoundError:
            existing = []
        if existing:
            versions = ", ".join(sorted(item.version for item in existing))
            print(f"Evaluator {args.name} already exists with version(s): {versions}.")
            print("No changes were made; use azd ai agent eval update for a new version.")
            return 0

        evaluator = project_client.beta.evaluators.create_version(
            name=args.name,
            evaluator_version={
                "name": args.name,
                "categories": [EvaluatorCategory.QUALITY],
                "display_name": args.display_name,
                "description": args.description,
                "definition": {
                    "type": EvaluatorDefinitionType.RUBRIC,
                    "dimensions": dimensions,
                    "pass_threshold": args.pass_threshold,
                },
            },
        )

    print(f"Created evaluator {evaluator.name} version {evaluator.version}.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
