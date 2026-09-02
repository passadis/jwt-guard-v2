#!/usr/bin/env python3
"""Register one immutable JWT Sentinel rubric evaluator version in Foundry."""

from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
from typing import Any

from azure.ai.projects import AIProjectClient
from azure.ai.projects.models import EvaluatorCategory, EvaluatorDefinitionType
from azure.core.exceptions import ResourceNotFoundError
from azure.identity import AzureCliCredential


def positive_version(value: str) -> int:
    version = int(value)
    if version < 1:
        raise argparse.ArgumentTypeError("version must be a positive integer")
    return version


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description=(
            "Validate a rubric locally and, with --apply, create exactly the "
            "next custom-evaluator version in a Foundry project. Existing "
            "versions are never updated or overwritten by this script."
        )
    )
    parser.add_argument("--project-endpoint", required=True)
    parser.add_argument("--name", required=True)
    parser.add_argument("--rubric", required=True, type=Path)
    parser.add_argument(
        "--expected-version",
        required=True,
        type=positive_version,
        help="Required auto-incremented version that the service must create.",
    )
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
        help="Create the expected next evaluator version. Without this flag, validate only.",
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

    rubric_path = args.rubric.resolve()
    dimensions = load_dimensions(rubric_path)
    rubric_sha256 = hashlib.sha256(rubric_path.read_bytes()).hexdigest()
    expected_version = str(args.expected_version)
    print(
        f"Validated {len(dimensions)} rubric dimensions for {args.name} "
        f"target version {expected_version} (SHA-256 {rubric_sha256})."
    )
    if not args.apply:
        print(
            "Dry run only; no Foundry evaluator was created. Apply will require "
            f"version {expected_version} to be the service's next version."
        )
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
        existing_by_version = {str(item.version): item for item in existing}
        if expected_version in existing_by_version:
            existing_metadata = getattr(
                existing_by_version[expected_version], "metadata", None
            ) or {}
            existing_hash = existing_metadata.get("rubric_sha256")
            if existing_hash != rubric_sha256:
                raise RuntimeError(
                    f"Evaluator {args.name} version {expected_version} already exists "
                    "with different or missing rubric provenance. Refusing to update "
                    "or overwrite the immutable version."
                )
            print(
                f"Evaluator {args.name} version {expected_version} already exists "
                "with the reviewed rubric hash. No changes were made."
            )
            return 0

        try:
            numeric_versions = sorted(int(version) for version in existing_by_version)
        except ValueError as exc:
            raise RuntimeError(
                "Evaluator has a nonnumeric version; refusing unsafe auto-increment."
            ) from exc

        next_version = str(numeric_versions[-1] + 1 if numeric_versions else 1)
        if next_version != expected_version:
            versions = ", ".join(str(version) for version in numeric_versions) or "none"
            raise RuntimeError(
                f"Evaluator {args.name} has version(s) {versions}; the service's next "
                f"version would be {next_version}, not required version "
                f"{expected_version}. No changes were made."
            )

        evaluator = project_client.beta.evaluators.create_version(
            name=args.name,
            evaluator_version={
                "name": args.name,
                "evaluator_type": "custom",
                "categories": [EvaluatorCategory.QUALITY],
                "display_name": args.display_name,
                "description": args.description,
                "metadata": {
                    "rubric_sha256": rubric_sha256,
                    "schema_version": expected_version,
                },
                "definition": {
                    "type": EvaluatorDefinitionType.RUBRIC,
                    "dimensions": dimensions,
                    "pass_threshold": args.pass_threshold,
                },
            },
        )

    if str(evaluator.version) != expected_version:
        raise RuntimeError(
            f"Foundry created unexpected evaluator version {evaluator.version}; "
            f"required {expected_version}. Stop before using this evaluator."
        )
    print(f"Created evaluator {evaluator.name} version {evaluator.version}.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
