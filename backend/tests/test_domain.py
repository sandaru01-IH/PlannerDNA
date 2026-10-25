"""Pure-domain checks: content hash vector shared with the C# test, and metric edge cases."""

import payloads
from app.domain.city import City, Cell, StableHash, city_from_dict
from app.domain.metrics import ScenarioRules, evaluate, park_distances_metres

# The same city and expected hash are pinned in Unity's CrossLanguageContractTests.HashVectorMatchesPython.
HASH_VECTOR_EXPECTED = "98a58308d3a8af2f"


def hash_vector_city() -> City:
    cells = (
        Cell(0, 0, "road", 0, 0, 0, False, "mixed"),
        Cell(0, 1, "residential", 3, 4, 8, True, "residential_only"),
        Cell(0, 2, "park", 0, 0, 0, True, "protected"),
        Cell(1, 0, "commercial", 2, 0, 6, True, "mixed"),
        Cell(1, 1, "vacant", 0, 0, 8, True, "mixed"),
        Cell(1, 2, "civic", 1, 0, 2, False, "mixed"),
    )
    return City("hash-vector", 7, "gen-x", "metrics-1.0.0", 1, 3, 2, 40, cells)


def test_hash_vector_is_pinned():
    assert hash_vector_city().content_hash() == HASH_VECTOR_EXPECTED


def test_stable_hash_primitives():
    assert StableHash().hex() == "cbf29ce484222325"  # FNV-1a 64 offset basis
    # Negative ints are hashed as their uint32 two's-complement bytes, like C#'s (uint) cast.
    assert StableHash().add_int(-1).hex() == StableHash().add_int(0xFFFFFFFF).hex()
    # Strings are length-prefixed UTF-8, so "ab"+"c" differs from "a"+"bc".
    assert StableHash().add_str("ab").add_str("c").hex() != StableHash().add_str("a").add_str("bc").hex()


def test_hash_changes_with_any_cell_property():
    base = hash_vector_city()
    changed = City(**{**base.__dict__, "cells": (base.cells[0], Cell(0, 1, "residential", 4, 4, 8, True, "residential_only"))
                      + base.cells[2:]})
    assert changed.content_hash() != base.content_hash()


def test_access_distance_counts_road_steps():
    # The only park is r02_c02; the only road touching it is r03_c02 (0 steps). Distance = 50 m to
    # step onto the road + 50 m per road step + 50 m into the park. Adjacency alone never counts.
    d = park_distances_metres(city_from_dict(payloads.city()))
    at = lambda r, c: d[r * 6 + c]  # noqa: E731
    assert at(4, 2) == 100   # beside r03_c02
    assert at(4, 1) == 150   # beside r03_c01 (1 step)
    assert at(2, 1) == 150   # touches the park, but counts via road r03_c01 (1 step), not adjacency
    assert at(1, 1) == 300   # best road r01_c00 is 4 steps away
    assert at(0, 0) == -1    # roads themselves get no distance


def test_access_undefined_without_housing():
    grid = ["RRR", "RvR", "RRR"]
    city = city_from_dict(payloads.city(grid=grid))
    m = evaluate(city, city, ScenarioRules(**payloads.RULES))
    assert m.housing_units == 0 and m.access_defined is False and m.access_pct == 0.0
