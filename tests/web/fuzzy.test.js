// Same cases as tests/SwVault.Core.Tests/FuzzySearchTests.cs: the web browser's search must behave
// like the SOLIDWORKS one. Run: node --test tests/web/
"use strict";
const test = require("node:test");
const assert = require("node:assert");
const Fuzzy = require("../../server/linux/invites/web/fuzzy.js");

const matches = [
  ["brack", "FrontBracket.SLDPRT"], ["BRACKET", "front_bracket.sldprt"], ["fbr", "Front_Bracket_Rev.SLDPRT"],
  ["frbrk", "FrontBracket.SLDPRT"], ["braket", "FrontBracket.SLDPRT"], ["bracekt", "FrontBracket.SLDPRT"],
  ["uprigth", "Upright_Front_Left.SLDPRT"], ["6061", "Aluminum 6061-T6"],
];
const rejects = [["xyz", "FrontBracket.SLDPRT"], ["bkt", "Manifold_Intake_Tube.SLDPRT"], ["brkt", ""], ["hub", "Hb.SLDPRT"]];

for (const [term, text] of matches) test(`matches ${term} in ${text}`, () => assert.ok(Fuzzy.match(term, text)));
for (const [term, text] of rejects) test(`rejects ${term} in ${text}`, () => assert.strictEqual(Fuzzy.match(term, text), null));

test("ranks substring above subsequence and typo", () => {
  const substring = Fuzzy.match("bracket", "FrontBracket.SLDPRT").score;
  assert.ok(substring > Fuzzy.match("frbrk", "FrontBracket.SLDPRT").score);
  assert.ok(substring > Fuzzy.match("brakcet", "FrontBracket.SLDPRT").score);
});

test("ranks word start above mid-word", () =>
  assert.ok(Fuzzy.match("arm", "Arm_Upper.SLDPRT").score > Fuzzy.match("arm", "Swarmbot.SLDPRT").score));

test("positions point at matched characters", () => {
  assert.deepStrictEqual(Fuzzy.match("brack", "FrontBracket").positions, [5, 6, 7, 8, 9]);
  assert.deepStrictEqual(Fuzzy.match("fb", "Front_Bracket").positions, [0, 6]);
});

test("splits words on separators, case and digits", () => {
  const text = "FrontBracket_rev2.SLDPRT";
  assert.deepStrictEqual(Fuzzy.words(text).map(([s, l]) => text.substr(s, l)), ["Front", "Bracket", "rev", "2", "SLDPRT"]);
});

const fields = (name, ...more) => [
  { label: "Name", value: name, weight: 1 }, { label: "Folder", value: "Suspension/Front", weight: 0.6 },
  ...more.map(([label, value]) => ({ label, value, weight: 0.85 })),
];

test("every term must match some field", () => {
  const f = fields("Upright.SLDPRT", ["Material", "7075-T6"]);
  assert.ok(Fuzzy.parseQuery("upright 7075").score(f));
  assert.ok(Fuzzy.parseQuery("7075 susp").score(f));
  assert.strictEqual(Fuzzy.parseQuery("upright titanium").score(f), null);
});

test("label:term searches one field", () => {
  const f = fields("Steel_Tab.SLDPRT", ["Material", "6061 Aluminum"]);
  assert.ok(Fuzzy.parseQuery("material:alum").score(f));
  assert.strictEqual(Fuzzy.parseQuery("material:steel").score(f), null);
  const hit = Fuzzy.parseQuery("mat:6061").score(f);
  assert.strictEqual(hit.matchLabel, "Material");
  assert.strictEqual(hit.matchValue, "6061 Aluminum");
});

test("name hits outrank property hits", () => {
  const byName = Fuzzy.parseQuery("hub").score(fields("Hub.SLDPRT"));
  const byProperty = Fuzzy.parseQuery("hub").score(fields("Part7.SLDPRT", ["Description", "Rear hub"]));
  assert.ok(byName.score > byProperty.score);
  assert.deepStrictEqual(byName.namePositions, [0, 1, 2]);
  assert.strictEqual(byName.matchLabel, null);
  assert.strictEqual(byProperty.matchLabel, "Description");
});

test("quoted terms keep spaces", () => {
  const f = fields("Part.SLDPRT", ["Description", "front left upright"]);
  assert.ok(Fuzzy.parseQuery('"left upright"').score(f));
  assert.strictEqual(Fuzzy.parseQuery('"upright left"').score(f), null);
});

test("empty query matches everything", () => assert.ok(Fuzzy.parseQuery("  ").score(fields("Any.SLDPRT"))));
