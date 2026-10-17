"""Counts as the analyzer writes them: a unit reads in the singular for one (NodeValue.Integer)."""


def bytes_text(count):
    return f"{count} byte" if count == 1 else f"{count} bytes"


def bits_text(count):
    return f"{count} bit" if count == 1 else f"{count} bits"
