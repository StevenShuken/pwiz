/*
 * Original author: Brian Pratt <bspratt .at. proteinms.net>,
 *                  MacCoss Lab, Department of Genome Sciences, UW
 *
 * Copyright 2016 University of Washington - Seattle, WA
 * 
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 *     http://www.apache.org/licenses/LICENSE-2.0I
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using pwiz.Common.Chemistry;
using pwiz.Common.Collections;
using pwiz.Common.SystemUtil;
using pwiz.Skyline.Properties;

namespace pwiz.Skyline.Util
{

    /// <summary>
    /// This collection class serves to replace the many places in code where we formerly 
    /// indexed items by (inherently positive integer) charge state.
    /// </summary>
    public class AdductMap<T>
    {
        private readonly Dictionary<Adduct, T> _dict;

        public AdductMap()
        {
            _dict = new Dictionary<Adduct, T>();
        }

        public T this[Adduct a]
        {
            get
            {
                T item;
                if (!_dict.TryGetValue(a, out item))
                {
                    item = default(T);
                    _dict.Add(a, item);
                }
                return item;
            }
            set
            {
                if (!_dict.ContainsKey(a))
                {
                    _dict.Add(a, value);
                }
                else
                {
                    _dict[a] = value;
                }
            }            
        }

        public IEnumerable<Adduct> Keys { get { return _dict.Keys; } }
    }

    public class Adduct : Immutable, IComparable, IEquatable<Adduct>
    {
        // CONSIDER(bspratt): Nick suggests we change this ImmutableDictionary to Molecule once that is performant, and supports negative counts
        private ImmutableDictionary<string, int> Composition { get; set; } // The chemical makeup of the adduct
        private string Description { get; set; } // The text description (will be empty for protonation, we just use charge)
        private ImmutableDictionary<string, KeyValuePair<string, int>> IsotopeLabels { get; set; } // Key is unlabeled atom, value is <LabeledAtom, count>
        private double? IsotopeLabelMass { get; set; } // Sometimes we are only given an incremental mass for label purposes (if we have isotope formula this is null)
        private TypedMass AverageMassAdduct { get; set; } // Average mass of the adduct itself - the "2H" in 4M3Cl37+2H
        private TypedMass MonoMassAdduct { get; set; } // Monoisotopic mass of the adduct itself - the "2H" in 4M3Cl37+2H
        private int MassMultiplier { get; set; } // Returns, for example, the 2 in "[2M+Na]", which means the ion is two molecules + the adduct mass. 
        private TypedMass IsotopesIncrementalAverageMass { get; set; } // The incremental average mass due to (4*3) (Cl37 - Cl) in 4M3Cl37+2H 
        private TypedMass IsotopesIncrementalMonoMass { get; set; } // The incremental mono mass due to (4*3) (Cl37 - Cl) in 4M3Cl37+2H

        private int _hashCode; // We want comparisons to be on the same order as comparing ints, as when we used to just use integer charge instead of proper adducts

        //
        // Note constructors are private - use FromCharge and FromString instead, which allow reuse
        //

        public enum ADDUCT_TYPE
        {
            proteomic, // parsing "2" results in an adduct w/ formula H2, z=2, displays as "2" or "++"
            non_proteomic, // parsing "2" results in an adduct w/ formula H2, z=2, displays as "[M+2H]"
            charge_only // parsing "2" results in an adduct w/ no formula, z=2, displays as "[M+2]" or "++"
        }

        private Adduct(int charge, bool protonated)
        {
            InitializeAsCharge(charge, protonated ? ADDUCT_TYPE.proteomic : ADDUCT_TYPE.charge_only);
            SetHashCode(); // For fast GetHashCode()
        }

        private Adduct(string description, ADDUCT_TYPE integerMode, int? explicitCharge = null) // Description should have form similar to M+H, 2M+2NA-H etc, or it may be a text representation of a protonated charge a la "2", "-3" etc
        {
            var input = (description ?? string.Empty).Trim();
            int chargeFromText;
            if (input.Length == 0)
            {
                // No text description
                InitializeAsCharge(explicitCharge ?? 0, integerMode);
            }
            else if (int.TryParse(input, out chargeFromText))
            {
                // Described purely as a charge
                InitializeAsCharge(chargeFromText, integerMode);
            }
            else
            {
                if (!input.StartsWith("[") && !input.Contains("[")) // Not L10N
                {
                    // Accept a bare "M+Na", but put it in canonical form "[M+Na]"
                    input = "[" + input + "]"; // Not L10N
                }
                ParseDescription(Description = input);
                InitializeMasses();
            }

            if (explicitCharge.HasValue)
            {
                if (AdductCharge != 0) // Does claimed charge agree with obviously calcuable charge?
                {
                    Assume.IsTrue(AdductCharge == explicitCharge, "Conflicting charge values in adduct description "+input ); // Not L10N
                }
                AdductCharge = explicitCharge.Value;
            }
            SetHashCode(); // For fast GetHashCode()
        }

        private void ParseDescription(string input)
        {
            const RegexOptions regexOptions =
                RegexOptions.ExplicitCapture | RegexOptions.Singleline | RegexOptions.CultureInvariant;
            var outerRegex =
                new Regex(
                    @"\[?(?<multM>\d*)M(?<label>(\(.*\)|[^\+\-]*))?(?<adduct>[\+\-][^\]]*)(\](?<declaredChargeCount>\d*)(?<declaredChargeSign>[+-]*)?)?$",
                    regexOptions); // Not L10N
            var innerRegex = new Regex(@"(?<oper>\+|\-)(?<multM>\d+)?(?<ion>[^-+]*)", regexOptions); // Not L10N
            int? declaredCharge = null;
            int? calculatedCharge = null;
            int parsedCharge;
            var match = outerRegex.Match(input.Trim());
            var adductOperations = match.Groups["adduct"].Value; // Not L10N
            var success = match.Success && (match.Groups.Count == 6) && !string.IsNullOrEmpty(adductOperations);

            // Check for sane bracketing (none, or single+balanced+anchored)
            if (success)
            {
                var brackets = input.Count(c => c == '[');
                success &= brackets == input.Count(c => c == ']');
                if (brackets != 0)
                {
                    success &= (brackets == 1) && input.StartsWith("["); // Not L10N
                }
            }
            var composition = new Dictionary<string, int>();
            if (success)
            {
                // Read the mass multiplier if any - the "2" in "[2M+..." if any such thing is there
                var massMultiplier = 1;
                var massMultiplierStr = match.Groups["multM"].Value; // Not L10N
                if (!string.IsNullOrEmpty(massMultiplierStr))
                {
                    success = int.TryParse(massMultiplierStr, out massMultiplier);
                }
                MassMultiplier = massMultiplier;

                // Read any isotope declarations

                // Read the "4Cl37" in "[2M4Cl37+..." if any such thing is there
                // Also deal with more complex labels, eg 6C132N15 -> 6C'2N'
                var label = match.Groups["label"].Value; // Not L10N
                var hasIsotopeLabels = !string.IsNullOrEmpty(label);
                if (hasIsotopeLabels)
                {
                    double labelMass;
                    label = label.Trim('(', ')'); // In case adduct had form like M(-1.2345)+H
                    if (double.TryParse(label, NumberStyles.Float, CultureInfo.InvariantCulture, out labelMass))
                    {
                        // Sometimes all we're given is a mass offset eg M1.002+2H
                        IsotopeLabelMass = labelMass;
                    }
                    else
                    {
                        label = DICT_ADDUCT_ISOTOPE_NICKNAMES.Aggregate(label, (current, nickname) => current.Replace(nickname.Key, nickname.Value)); // eg Cl37 -> Cl'
                        // Problem: normal chemical formula for "6C132N15H" -> "6C'2NH'" would be "C'6N'15H"
                        var ionRegex = new Regex(@"(?<multM>\d+)?(?<ion>[A-Z][a-z]?['\""]?)", regexOptions); // Not L10N
                        var ionMatches = ionRegex.Matches(label);
                        var isotopeLabels = new Dictionary<string, KeyValuePair<string, int>>();
                        foreach (Match m in ionMatches)
                        {
                            if (m.Groups.Count < 1)
                            {
                                success = false;
                                break;
                            }
                            var multiplierM = 1;
                            var multMstr = m.Groups["multM"].Value; // Read the "2" in "+2H" if any such thing is there // Not L10N
                            if (!string.IsNullOrEmpty(multMstr))
                            {
                                success = int.TryParse(multMstr, out multiplierM);
                            }
                            var isotope = m.Groups["ion"].Value; // Not L10N
                            var unlabel = isotope.Replace("'", "").Replace("\"", ""); // Not L10N
                            isotopeLabels.Add(unlabel, new KeyValuePair<string, int>(isotope, multiplierM));
                        }
                        IsotopeLabels = new ImmutableDictionary<string, KeyValuePair<string, int>>(isotopeLabels);
                    }
                }

                var declaredChargeCountStr = match.Groups["declaredChargeCount"].Value; // Not L10N
                if (!string.IsNullOrEmpty(declaredChargeCountStr)) // Read the "2" in "[M+H+Na]2+" if any such thing is there
                {
                    if (!string.IsNullOrEmpty(declaredChargeCountStr))
                    {
                        int z;
                        success = int.TryParse(declaredChargeCountStr, out z);
                        declaredCharge = z;
                    }
                }
                var declaredChargeSignStr = match.Groups["declaredChargeSign"].Value; // Not L10N
                if (!string.IsNullOrEmpty(declaredChargeSignStr))
                    // Read the "++" in "[M+2H]++" or "+" in "]2+" if any such thing is there
                {
                    declaredCharge = (declaredCharge ?? 1)*
                                     (declaredChargeSignStr.Count(c => c == '+') - declaredChargeSignStr.Count(c => c == '-'));
                }
                if (adductOperations.StartsWith("+") && adductOperations.Distinct().Count() == 1) // "[M+]" is legit, "[M+++]" etc is presumably also legit // Not L10N
                {
                    calculatedCharge = adductOperations.Length;
                }
                else if (adductOperations.StartsWith("-") && adductOperations.Distinct().Count() == 1) // "[M-]", "[M---]" etc are presumably also legit // Not L10N
                {
                    calculatedCharge = -adductOperations.Length;
                }
                else if (int.TryParse(adductOperations, out parsedCharge)) // "[M+2]", "[M-3]" etc
                {
                    calculatedCharge = parsedCharge;
                }
                else
                {
                    // Now parse each part of the "+Na-2H" in "[M+Na-2H]" if any such thing is there
                    var matches = innerRegex.Matches(adductOperations);
                    int remaining = matches.Count;
                    foreach (Match m in matches)
                    {
                        remaining--;
                        if (m.Groups.Count < 4)
                        {
                            success = false;
                            break;
                        }
                        var multiplierM = 1;
                        var multMstr = m.Groups["multM"].Value; // Read the "2" in "+2H" if any such thing is there // Not L10N
                        if (!string.IsNullOrEmpty(multMstr))
                        {
                            success = int.TryParse(multMstr, out multiplierM);
                        }
                        if (m.Groups["oper"].Value.Contains("-")) // Not L10N
                        {
                            multiplierM *= -1;
                        }
                        var ion = m.Groups["ion"].Value; // Not L10N
                        int ionCharge;
                        if (DICT_ADDUCT_ION_CHARGES.TryGetValue(ion, out ionCharge))
                        {
                            calculatedCharge = (calculatedCharge ?? 0) + ionCharge*multiplierM;
                        }

                        // Swap common nicknames like "DMSO" for "C2H6OS", or "N15" for N'
                        string realname;
                        if (DICT_ADDUCT_NICKNAMES.TryGetValue(ion, out realname) || DICT_ADDUCT_ISOTOPE_NICKNAMES.TryGetValue(ion, out realname))
                        {
                            ion = realname;
                        }
                        var ionMolecule = Molecule.Parse(ion);
                        if (ionMolecule.Count == 0)
                        {
                            success = multiplierM == 1 && remaining != 0; // Allow pointless + in "M+-H2O+H" but not trailing +in "M-H2O+H+"
                        }
                        foreach (var pair in ionMolecule)
                        {
                            int count;
                            if (composition.TryGetValue(pair.Key, out count))
                            {
                                composition[pair.Key] = count + pair.Value * multiplierM;
                            }
                            else
                            {
                                composition.Add(pair.Key, pair.Value * multiplierM);
                            }
                        }
                    }
                }
            }
            AdductCharge = calculatedCharge ?? declaredCharge ?? 0;
            Composition = new ImmutableDictionary<string, int>(composition);
            var resultMol = Molecule.FromDict(new ImmutableSortedList<string, int>(composition));
            if (!resultMol.Keys.All(k => BioMassCalc.MONOISOTOPIC.IsKnownSymbol(k)))
            {
                throw new InvalidOperationException(
                    string.Format(Resources.BioMassCalc_ApplyAdductToFormula_Unknown_symbol___0___in_adduct_description___1__,
                        resultMol.Keys.First(k => !BioMassCalc.MONOISOTOPIC.IsKnownSymbol(k)), input));
            }
            if (!success)
            {
                if (Equals("[M]", input)) // Allow trivial charge free adduct  // Not L10N
                {
                    MassMultiplier = 1;
                }
                else
                {
                    throw new InvalidOperationException(
                        string.Format(Resources.BioMassCalc_ApplyAdductToFormula_Failed_parsing_adduct_description___0__, input));
                }
            }
            if (declaredCharge.HasValue && calculatedCharge.HasValue && declaredCharge != calculatedCharge)
            {
                throw new InvalidOperationException(
                    string.Format(
                        Resources
                            .BioMassCalc_ApplyAdductToFormula_Failed_parsing_adduct_description___0____declared_charge__1__does_not_agree_with_calculated_charge__2_,
                        input, declaredCharge.Value, calculatedCharge));
            }
        }
        public Adduct Unlabeled { get; private set; } // Version of this adduct without any isotope labels

        // N.B. "AdductCharge" and "AdductFormula" seem like weirdly redundant names, until you consider that 
        // they can show up in reports, at which point "Charge" and "Formula" are a bit overloaded.

        public int AdductCharge { get; private set; }  // The charge that the adduct gives to a molecule

        public string AdductFormula // Return adduct description - will produce [M+H] format for protonation
        {
            get
            {
                if (IsEmpty)
                {
                    return string.Empty;
                }
                if (IsProteomic) // We don't carry description for peptide protonation, generate one here
                {
                    switch (AdductCharge)
                    {
                        case 1:
                            return "[M+H]"; // Not L10N
                        case -1:
                            return "[M-H]"; // Not L10N
                        default:
                            return string.Format("[M{0:+#;-#}H]", AdductCharge); // Not L10N
                    }
                }
                return Description;
            }
        }

        public bool IsProtonated { get; private set; } // When true, we use a slightly different mz calc for backward compatibility

        public bool IsChargeOnly { get { return Composition.Count == 0 && !HasIsotopeLabels; } }

        public bool IsProteomic { get; private set; } //  For peptide use

        public bool IsEmpty { get { return ReferenceEquals(this, EMPTY); } }

        public bool HasIsotopeLabels { get { return (IsotopeLabelMass ?? 0) != 0 || (IsotopeLabels != null && IsotopeLabels.Count > 0); } } // Does the adduct description include isotopes, like "6Cl37" in "M6Cl37+2H"


        // Helper function for UI - does this string look like it's on its way to being an adduct?
        public static bool PossibleAdductDescriptionStart(string possibleDescription)
        {
            if (string.IsNullOrEmpty(possibleDescription))
                return false;
            Adduct val;
            if (TryParse(possibleDescription, out val) && !val.IsEmpty)
            {
                return true; // An actual adduct description
            }
            return possibleDescription.StartsWith("[") || possibleDescription.StartsWith("M"); // Not L10N
        }

        /// <summary>
        /// Construct an adduct based on a string (probably serialized XML) of form "2" or "-3" or "[M+Na]" etc.
        /// Minimizes memory thrash by reusing the more common adducts. 
        /// Assumes protonated adduct when dealing with charge only, 
        /// so "2" gives adduct z=2 formula=H2 ToString="++".
        /// </summary>
        public static Adduct FromStringAssumeProtonated(string value)
        {
            return FromString(value, ADDUCT_TYPE.proteomic, null);
        }
        /// Same as above, but assumes charge-only adduct when dealing with integer charge only,
        /// so "2" gives adduct z=2 formula=<none></none> ToString="[M+2]".
        public static Adduct FromStringAssumeChargeOnly(string value)
        {
            return FromString(value, ADDUCT_TYPE.charge_only, null);
        }

        /// Same as above, but assumes charge-only adduct when dealing with integer charge only,
        /// so "2" gives adduct z=2 formula=H2 ToString="[M+2H]".
        public static Adduct FromStringAssumeProtonatedNonProteomic(string value)
        {
            return FromString(value, ADDUCT_TYPE.non_proteomic, null);
        }

        /// <summary>
        /// Construct an adduct based on a string (probably serialized XML) of form "2" or "-3" or "[M+Na]" etc.
        /// Minimizes memory thrash by reusing the more common adducts. 
        ///
        /// </summary>
        public static Adduct FromString(string value, ADDUCT_TYPE parserMode, int? explicitCharge)
        {
            if (value == null)
                return EMPTY;
            int z;
            if (int.TryParse(value, out z))
            {
                return FromCharge(z, parserMode);
            }

            // Reuse the more common non-proteomic adducts
            var testValue = value.StartsWith("M") ? "[" + value + "]" : value; // Not L10N
            var testAdduct = new Adduct(testValue, parserMode, explicitCharge);
            if (!testValue.EndsWith("]"))
            {
                // Can we trim any trailing charge info to arrive at a standard form (ie use [M+H] instead of [M+H]+)?
                try
                {
                    var stripped = testValue.Substring(0, testValue.IndexOf(']')+1);
                    var testB = new Adduct(stripped, parserMode, explicitCharge);
                    if (testAdduct.SameEffect(testB))
                        testAdduct = testB; // Go with the simpler canonical form
                }
                catch
                {
                    // ignored
                }
            }
            // Re-use the standard pre-allocated adducts when possible
            foreach (var adduct in (parserMode == ADDUCT_TYPE.proteomic) ? COMMON_PROTONATED_ADDUCTS : COMMON_SMALL_MOL_ADDUCTS) 
            {
                if (testAdduct.SameEffect(adduct))
                {
                    return adduct;
                }
            }
            return testAdduct;
        }

        /// <summary>
        /// Given, for example, charge=-3, return an Adduct with z=-3, empty formula, displays as "[M-3]"
        /// </summary>
        public static Adduct FromChargeNoMass(int charge)
        {
            switch (charge)
            {
                case 0:
                    return EMPTY;
                case 1:
                    return M_PLUS; // [M+]
                case -1:
                    return M_MINUS; // [M-]
                case 2:
                    return M_PLUS_2; // [M+2]
                case -2:
                    return M_MINUS_2; // [M-2]
                case 3:
                    return M_PLUS_3; // [M+3]
                case -3:
                    return M_MINUS_3; // [M-3]
                default:
                    return new Adduct(string.Format("[M{0:+#;-#}]", charge), ADDUCT_TYPE.charge_only, charge); // Not L10N
            }
        }

        /// <summary>
        /// Given, for example, charge=3, return an Adduct with z=3, formula = H3, displays as "[M+3H]"
        /// </summary>
        public static Adduct NonProteomicProtonatedFromCharge(int charge)
        {
            if (charge == 0)
                return EMPTY;
            var adductTmp = FromChargeProtonated(charge);
            return new Adduct(adductTmp.AdductFormula, ADDUCT_TYPE.non_proteomic, charge);  // Create an adduct that shows a formula in ToString()
        }

        /// <summary>
        /// Same as NonProteomicProtonatedFromCharge(int charge), but also accepts isotope information
        /// </summary>
        public static Adduct NonProteomicProtonatedFromCharge(int charge, IDictionary<string, int> dictIsotopeCounts)
        {
            var adductTmp = NonProteomicProtonatedFromCharge(charge);
            if (dictIsotopeCounts != null && dictIsotopeCounts.Count > 0)
            {
               // Convert from our chemical formula syntax to that used by adducts
                var adductIons = dictIsotopeCounts.Aggregate("[M", (current, pair) => current + string.Format(CultureInfo.InvariantCulture, "{0}{1}", // Not L10N
                    (pair.Value>1) ? pair.Value.ToString() : string.Empty, 
                    (DICT_ADDUCT_NICKNAMES.FirstOrDefault(x => x.Value == pair.Key).Key ?? DICT_ADDUCT_ISOTOPE_NICKNAMES.FirstOrDefault(x => x.Value == pair.Key).Key) ??pair.Key));
                return new Adduct(adductIons + adductTmp.AdductFormula.Substring(2), ADDUCT_TYPE.non_proteomic, charge);
            }
            if (charge == 0)
            {
                return EMPTY;
            }
            return new Adduct(adductTmp.AdductFormula, ADDUCT_TYPE.non_proteomic, charge);  // Create an adduct that shows a formula in ToString()
        }

        public static Adduct FromFormulaDiff(string left, string right, int charge)
        {
            // Take adduct as the difference between two chemical formulas
            var l = Molecule.Parse(left.Trim());
            var r = Molecule.Parse(right.Trim());
            var adductFormula = l.Difference(r).ToString();
            return new Adduct("[M+" + adductFormula + "]", ADDUCT_TYPE.non_proteomic) { AdductCharge = charge }; // Not L10N
        }

        /// <summary>
        /// Replace, for example, the "2" in "[2M+H]"
        /// </summary>
        public Adduct ChangeMassMultiplier(int value)
        {
            if (value == MassMultiplier)
                return this; // No change
            var indexM = AdductFormula.IndexOf('M');
            if (indexM < 1)
                return this;
            var newFormula = (value > 1 ? string.Format("[{0}", value) : "[") + AdductFormula.Substring(indexM); // Not L10N
            return Equals(AdductFormula, newFormula) ? this : new Adduct(newFormula, ADDUCT_TYPE.non_proteomic, AdductCharge);
        }

        private Adduct ChangeIsotopeLabels(string isotopes)
        {
            if (string.IsNullOrEmpty(isotopes) && !HasIsotopeLabels)
            {
                return this;
            }
            var indexM = AdductFormula.IndexOf('M');
            if (indexM < 1)
            {
                return this;
            }
            var signIndex = FindSignIndex(AdductFormula);
            if (signIndex < 0)
            {
                return EMPTY; // Error
            }
            var newFormula = AdductFormula.Substring(0, indexM + 1) + isotopes + AdductFormula.Substring(signIndex);
            return Equals(AdductFormula, newFormula) ? this : new Adduct(newFormula, ADDUCT_TYPE.non_proteomic, AdductCharge); // No reason isotopes should change charge
        }


        /// <summary>
        /// Replace, for example, the "6C13" in "[M6C13+Na]"
        /// Accepts a dictionary of isotope,count where isotope is either in Skyline vernacular Cl', or adduct-speak Cl37
        /// </summary>
        public Adduct ChangeIsotopeLabels(IDictionary<string, int> isotopes)
        {
            if ((isotopes==null || isotopes.Count==0) && !HasIsotopeLabels)
            {
                return this;
            }
            return ChangeIsotopeLabels(
                isotopes == null || isotopes.Count == 0 ? string.Empty : isotopes.Aggregate(string.Empty,
                        (current, pair) => current + string.Format(CultureInfo.InvariantCulture, "{0}{1}", // Not L10N
                            (pair.Value > 1) ? pair.Value.ToString() : string.Empty,
                            // If label was described (for example) as Cl' in dict, look up Cl37 and use that
                            DICT_ADDUCT_ISOTOPE_NICKNAMES.FirstOrDefault(x => x.Value == pair.Key).Key ?? pair.Key))); 
        }

        // Sometimes all we know is that two analytes have same name but different masses - describe isotope label as a mass
        public Adduct ChangeIsotopeLabels(double value)
        {
            if (value < 0)
            {
                return ChangeIsotopeLabels(string.Format("({0})", value.ToString(".0####", CultureInfo.InvariantCulture))); // Not L10N
            }
            return ChangeIsotopeLabels(value==0 ? string.Empty : value.ToString(".0####", CultureInfo.InvariantCulture)); // Not L10N
        }

        // Change the charge multiplier if possible
        // ie for charge 2, [M+Na] -> [M+2Na] but [M+3Na-H] goes to [M+2H] because it's ambiguous
        public Adduct ChangeCharge(int newCharge)
        {
            if (Equals(newCharge, AdductCharge))
                return this;

            var formula = AdductFormula;
            var signIndex = FindSignIndex(formula); // Skip over any isotope description - might contain "-"
            if (signIndex > 0)
            {
                if (formula.Substring(signIndex).Count(c => c == '+' || c == '-') == 1) // Reject multipart adducts - don't know which parts to change
                {
                    var oldcount = formula.Substring(signIndex, 1) + new string(formula.Substring(signIndex + 1).TakeWhile(char.IsDigit).ToArray()); // Find the +2 in [M+2Na] or the + in [M+H]
                    var newcount = (newCharge < 0 ? "-" : "+") + (Math.Abs(newCharge) > 1 ? Math.Abs(newCharge).ToString(CultureInfo.InvariantCulture) : string.Empty); // Not L10N
                    formula = formula.Substring(0,signIndex) + formula.Substring(signIndex).Replace(oldcount, newcount);
                    Adduct result;
                    if (TryParse(formula, out result))
                    {
                        if (result.AdductCharge == newCharge)
                        {
                            return result; // Revised charge looks good
                        }
                        if (result.AdductCharge == -newCharge)
                        {
                            // Revised charge is opposite of what we expected - adduct has opposite charge value of what we expected?
                            formula = formula.Substring(0, signIndex) + formula.Substring(signIndex).Replace(newCharge < 0 ? "-" : "+", newCharge < 0 ? "+" : "-"); // Not L10N
                            if (TryParse(formula, out result) && result.AdductCharge == newCharge)
                            {
                                return result;
                            }
                        }
                    }
                }
            }
            throw new InvalidOperationException(string.Format("Unable to adjust adduct formula {0} to achieve charge state {1}", AdductFormula, newCharge)); // Not L10N
        }

        /// <summary>
        /// Replace, for example, the "+Na" in "[M+Na]"
        /// </summary>
        public Adduct ChangeIonFormula(string val)
        {
            var end = AdductFormula.IndexOf(']');
            if (end < 0)
                return this;
            var formula = AdductFormula.Substring(0, end);
            var signIndex = FindSignIndex(formula);
            if (signIndex < 0) 
                return EMPTY;
            if (string.IsNullOrEmpty(val))
            {
                signIndex++; // Include a charge sense for parsability
            }
            var newFormula = formula.Substring(0, signIndex) + (val??string.Empty) + "]"; // Not L10N
            return Equals(AdductFormula, newFormula) ? this : new Adduct(newFormula, ADDUCT_TYPE.non_proteomic);
        }

        private int FindSignIndex(string formula)
        {
            var closeNumericIsotopeDescription = formula.IndexOf(')');
            if (closeNumericIsotopeDescription > 0)
            {
                // Skip over the (-1.2345) in "M(-1.2345)+2H"
                formula = formula.Substring(closeNumericIsotopeDescription);
            }
            else
            {
                closeNumericIsotopeDescription = 0;
            }
            var firstPlus = formula.IndexOf('+');
            var firstMinus = formula.IndexOf('-');
            if (firstPlus < 0)
                firstPlus = firstMinus;
            if (firstMinus < 0)
                firstMinus = firstPlus;
            var signIndex = Math.Min(firstPlus, firstMinus);
            return signIndex >= 0 ? signIndex + closeNumericIsotopeDescription : signIndex;
        }

        public static Adduct FromChargeProtonated(int? charge)
        {
            return charge.HasValue ? FromChargeProtonated(charge.Value) : EMPTY;
        }

        public static Adduct FromChargeProtonated(int charge)
        {
            return FromCharge(charge, ADDUCT_TYPE.proteomic);
        }

        public static Adduct FromCharge(int charge, ADDUCT_TYPE type)
        {
            var assumeProteomic = false;
            if (type == ADDUCT_TYPE.proteomic)
            {
                assumeProteomic = true;
                switch (charge)
                {
                    case 0:
                        return EMPTY;
                    case 1:
                        return SINGLY_PROTONATED;
                    case 2:
                        return DOUBLY_PROTONATED;
                    case 3:
                        return TRIPLY_PROTONATED;
                    case 4:
                        return QUADRUPLY_PROTONATED;
                    case 5:
                        return QUINTUPLY_PROTONATED;
                }
            }
            else if (type == ADDUCT_TYPE.non_proteomic)
            {
                switch (charge)
                {
                    case 0:
                        return EMPTY;
                    case 1:
                        return M_PLUS_H;
                    case 2:
                        return M_PLUS_2H;
                    case 3:
                        return M_PLUS_3H;
                    case -1:
                        return M_MINUS_H;
                    case -2:
                        return M_MINUS_2H;
                    case -3:
                        return M_MINUS_3H;
                }
            }
            else
            {
                switch (charge)
                {
                    case 0:
                        return EMPTY;
                    case 1:
                        return M_PLUS;
                    case 2:
                        return M_PLUS_2;
                    case 3:
                        return M_PLUS_3;
                    case -1:
                        return M_MINUS;
                    case -2:
                        return M_MINUS_2;
                    case -3:
                        return M_MINUS_3;
                }
            }
            return new Adduct(charge, assumeProteomic);
        }

        public static Adduct[] ProtonatedFromCharges(params int[] list)
        {
            return list.Select(FromChargeProtonated).ToArray();
        }

        public static bool TryParse(string s, out Adduct result, ADDUCT_TYPE assumeAdductType = ADDUCT_TYPE.non_proteomic)
        {
            result = EMPTY;
            try
            {
                result = FromString(s, assumeAdductType, null);
                return result.AdductCharge != 0;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Some internals made public for test purposes
        /// </summary>
        public int GetMassMultiplier() { return MassMultiplier; }
        public TypedMass GetIsotopesIncrementalAverageMass() { return IsotopesIncrementalAverageMass; }
        public TypedMass GetIsotopesIncrementalMonoisotopicMass() { return IsotopesIncrementalMonoMass; }

        // Common terms for small molecule adducts per http://fiehnlab.ucdavis.edu/staff/kind/Metabolomics/MS-Adduct-Calculator/ESI-MS-adducts.xls
        // See also (An interesting list of pseudoelements is at http://winter.group.shef.ac.uk/chemputer/pseudo-elements.html for a longer list we may wish to implement later
        public static readonly IDictionary<string, string> DICT_ADDUCT_NICKNAMES =
            new Dictionary<string, string>
            {
                // ReSharper disable NonLocalizedString
                {"ACN", "C2H3N"}, // Acetonitrile
                {"DMSO", "C2H6OS"}, // Dimethylsulfoxide
                {"FA", "CH2O2"}, // Formic acid
                {"Hac", "CH3COOH"}, // Acetic acid
                {"TFA", "C2HF3O2"}, // Trifluoroacetic acid
                {"IsoProp", "C3H8O"}, // Isopropanol
                {"MeOH", "CH3OH"}, // CH3OH. methanol
            };

        public static readonly IDictionary<string, string> DICT_ADDUCT_ISOTOPE_NICKNAMES =
            new Dictionary<string, string>
            {
                {"Cl37", BioMassCalc.Cl37},
                {"Br81", BioMassCalc.Br81},
                {"H2", BioMassCalc.H2},
                {"C13", BioMassCalc.C13},
                {"N15", BioMassCalc.N15},
                {"O17", BioMassCalc.O17},
                {"O18", BioMassCalc.O18}
                // ReSharper restore NonLocalizedString
            };

        // Ion charges seen in XCMS public and ESI-MS-adducts.xls
        public static readonly IDictionary<string, int> DICT_ADDUCT_ION_CHARGES =
            new Dictionary<string, int>
            {
                {BioMassCalc.H, 1},
                {BioMassCalc.K, 1},
                {BioMassCalc.Na, 1},
                {BioMassCalc.Li, 1},
                {BioMassCalc.Br,-1},
                {BioMassCalc.Cl,-1},
                {BioMassCalc.F, -1},
                {"CH3COO", -1}, // Deprotonated Hac // Not L10N
                {"HCOO", -1}, // Formate (deprotonated FA)   // Not L10N
                {"NH4", 1} // Not L10N
            };

        // Popular adducts (declared way down here because it has to follow some other statics)
        public static readonly Adduct EMPTY = new Adduct(0, false);
        public static readonly Adduct SINGLY_PROTONATED = new Adduct(1, true); // For use with proteomic molecules where user expects to see "z=1" instead  of "M+H" as the description
        public static readonly Adduct DOUBLY_PROTONATED = new Adduct(2, true);
        public static readonly Adduct TRIPLY_PROTONATED = new Adduct(3, true);
        public static readonly Adduct QUADRUPLY_PROTONATED = new Adduct(4, true);
        public static readonly Adduct QUINTUPLY_PROTONATED = new Adduct(5, true);

        public static readonly Adduct[] COMMON_PROTONATED_ADDUCTS =
        {
            SINGLY_PROTONATED,
            DOUBLY_PROTONATED,
            TRIPLY_PROTONATED,
            QUADRUPLY_PROTONATED,
            QUINTUPLY_PROTONATED
        };

        // Common small molecule adducts
        public static readonly Adduct M_PLUS_H = new Adduct("[M+H]", ADDUCT_TYPE.non_proteomic); // Not L10N
        public static readonly Adduct M_PLUS_Na = new Adduct("[M+Na]", ADDUCT_TYPE.non_proteomic); // Not L10N
        public static readonly Adduct M_PLUS_2H = new Adduct("[M+2H]", ADDUCT_TYPE.non_proteomic); // Not L10N
        public static readonly Adduct M_PLUS_3H = new Adduct("[M+3H]", ADDUCT_TYPE.non_proteomic); // Not L10N
        public static readonly Adduct M_PLUS = new Adduct("[M+]", ADDUCT_TYPE.non_proteomic); // Not L10N
        public static readonly Adduct M_PLUS_2 = new Adduct("[M+2]", ADDUCT_TYPE.non_proteomic); // Not L10N
        public static readonly Adduct M_PLUS_3 = new Adduct("[M+3]", ADDUCT_TYPE.non_proteomic); // Not L10N
        public static readonly Adduct M_MINUS_H = new Adduct("[M-H]", ADDUCT_TYPE.non_proteomic); // Not L10N
        public static readonly Adduct M_MINUS_2H = new Adduct("[M-2H]", ADDUCT_TYPE.non_proteomic); // Not L10N
        public static readonly Adduct M_MINUS_3H = new Adduct("[M-3H]", ADDUCT_TYPE.non_proteomic); // Not L10N
        public static readonly Adduct M_MINUS = new Adduct("[M-]", ADDUCT_TYPE.non_proteomic); // Not L10N
        public static readonly Adduct M_MINUS_2 = new Adduct("[M-2]", ADDUCT_TYPE.non_proteomic); // Not L10N
        public static readonly Adduct M_MINUS_3 = new Adduct("[M-3]", ADDUCT_TYPE.non_proteomic); // Not L10N

        public static readonly Adduct[] COMMON_SMALL_MOL_ADDUCTS =
        {
            M_PLUS_H,
            M_MINUS_H,
            M_PLUS_Na,
            M_PLUS_2H,
            M_PLUS_3H,
            M_PLUS,
            M_PLUS_2,
            M_PLUS_3,
            M_MINUS_2H,
            M_MINUS_3H,
            M_MINUS,
            M_MINUS_2,
            M_MINUS_3
        };

        public static readonly string[] COMMON_CHARGEONLY_ADDUCTS =
        {
            "[M+]",      // Not L10N
            "[M+2]",     // Not L10N
            "[M+3]",     // Not L10N
            "[M-]",      // Not L10N
            "[M-2]",     // Not L10N
            "[M-3]"      // Not L10N
        };

        // All the adducts from http://fiehnlab.ucdavis.edu/staff/kind/Metabolomics/MS-Adduct-Calculator
        public static readonly string[] DEFACTO_STANDARD_ADDUCTS =
        {
            "[M+3H]",        // Not L10N
            "[M+2H+Na]",     // Not L10N
            "[M+H+2Na]",     // Not L10N
            "[M+3Na]",       // Not L10N
            "[M+2H]",        // Not L10N
            "[M+H+NH4]",     // Not L10N
            "[M+H+Na]",      // Not L10N
            "[M+H+K]",       // Not L10N
            "[M+ACN+2H]",    // Not L10N
            "[M+2Na]",       // Not L10N
            "[M+2ACN+2H]",   // Not L10N
            "[M+3ACN+2H]",   // Not L10N
            "[M+H]",         // Not L10N
            "[M+NH4]",       // Not L10N
            "[M+Na]",        // Not L10N
            "[M+CH3OH+H]",   // Not L10N
            "[M+K]",         // Not L10N
            "[M+ACN+H]",     // Not L10N
            "[M+2Na-H]",     // Not L10N
            "[M+IsoProp+H]", // Not L10N
            "[M+ACN+Na]",    // Not L10N
            "[M+2K-H]",      // Not L10N
            "[M+DMSO+H]",    // Not L10N
            "[M+2ACN+H]",    // Not L10N
            "[M+IsoProp+Na+H]", // Not L10N
            "[2M+H]",           // Not L10N
            "[2M+NH4]",         // Not L10N
            "[2M+Na]",          // Not L10N
            "[2M+K]",           // Not L10N
            "[2M+ACN+H]",       // Not L10N
            "[2M+ACN+Na]",      // Not L10N
            "[M-3H]",        // Not L10N
            "[M-2H]",        // Not L10N
            "[M-H2O-H]",     // Not L10N
            "[M-H]",         // Not L10N
            "[M+Na-2H]",     // Not L10N
            "[M+Cl]",        // Not L10N
            "[M+K-2H]",      // Not L10N
            "[M+FA-H]",      // Not L10N
            "[M+Hac-H]",     // Not L10N
            "[M+Br]",        // Not L10N
            "[M+TFA-H]",     // Not L10N
            "[2M-H]",        // Not L10N
            "[2M+FA-H]",     // Not L10N
            "[2M+Hac-H]",    // Not L10N
            "[3M-H]"         // Not L10N
        };

        /// <summary>
        /// Generate a tooltip string that look something like this:
        ///
        ///   Formula may contain an adduct description (e.g. "C47H51NO14[M+IsoProp+H]").
        ///
        ///   Multipliers (e.g. "[2M+K]") and labels (e.g. "[M2Cl37+H]") are supported.
        ///   
        ///   Recognized adduct components include normal chemical symbols and:
        ///   ACN (C2H3N)
        ///   DMSO (C2H6OS)
        ///   FA (CH2O2)
        ///   Hac (CH3COOH)
        ///   TFA (C2HF3O2)
        ///   IsoProp (C3H8O)
        ///   MeOH (CH3OH)
        ///   Cl37 (Cl')
        ///   Br81 (Br')
        ///   C13 (C')
        ///   N15 (N')
        ///   O17 (O")
        ///   O18 (O').
        ///   
        ///   Charge states are inferred from the presence of these adduct components:
        ///   H (+1)
        ///   K (+1)
        ///   Na (+1)
        ///   Li (+1)
        ///   Br (-1)
        ///   Cl (-1)
        ///   F (-1)
        ///   CH3COO (-1)
        ///   NH4 (+1)
        /// 
        /// </summary>
        public static string Tips
        {
            get
            {
                var components = DICT_ADDUCT_NICKNAMES.Aggregate<KeyValuePair<string, string>, string>(null, (current, c) => current + (String.IsNullOrEmpty(current) ? "\r\n" : ", ") + String.Format("{0} ({1})", c.Key, c.Value)); // Not L10N
                components += DICT_ADDUCT_ISOTOPE_NICKNAMES.Aggregate<KeyValuePair<string, string>, string>(null, (current, c) => current + ", " + String.Format("{0} ({1})", c.Key, c.Value)); // Not L10N
                var chargers = DICT_ADDUCT_ION_CHARGES.Aggregate<KeyValuePair<string, int>, string>(null, (current, c) => current + (String.IsNullOrEmpty(current) ? "\r\n" : ", ") + String.Format("{0} ({1:+#;-#;+0})", c.Key, c.Value)); // Not L10N
                return string.Format(Resources.IonInfo_AdductTips_, components, chargers);
            }
        }

        public Dictionary<string, int> ApplyToMolecule(IDictionary<string, int> molecule)
        {
            var resultDict = new Dictionary<string, int>();
            ApplyToMolecule(molecule, resultDict);
            return resultDict;
        }

        /// <summary>
        /// Handle the "2" and "4Cl37" in "[2M4Cl37+H]", and add the H
        /// </summary>
        public void ApplyToMolecule(IDictionary<string, int> molecule, IDictionary<string, int> resultDict)
        {
            if (IsotopeLabels != null && IsotopeLabels.Count != 0 && molecule.Keys.Any(BioMassCalc.ContainsIsotopicElement))
            {
                // Don't apply labels twice
                Unlabeled.ApplyToMolecule(molecule, resultDict);
                return;
            }
            // Deal with any mass multipler (the 2 in "[2M+Na]") or labeling (the "4Cl37" in "[M4Cl37+2H]")
            foreach (var pair in molecule)
            {
                KeyValuePair<string, int> isotope;
                if (IsotopeLabels != null && 
                    IsotopeLabels.TryGetValue(pair.Key, out isotope))
                {
                    // If label is "2Cl37" and molecule is CH4Cl5 then result is CH4Cl3Cl'2
                    var unlabelCount = pair.Value - isotope.Value;
                    if (unlabelCount > 0)
                    {
                        resultDict.Add(pair.Key, MassMultiplier * unlabelCount);
                    }
                    else if (unlabelCount < 0)
                    {
                        throw new InvalidOperationException(
                            string.Format(Resources.BioMassCalc_ApplyAdductToFormula_Adduct_description___0___calls_for_more_labeled__1__than_are_found_in_the_molecule,
                                this, pair.Key));
                    }
                    int exist;
                    molecule.TryGetValue(isotope.Key, out exist);
                    resultDict.Add(isotope.Key, exist + (MassMultiplier * isotope.Value));
                }
                else
                {
                    resultDict.Add(pair.Key, MassMultiplier * pair.Value);
                }
            }

            foreach (var pair in Composition)
            {
                int count;
                if (resultDict.TryGetValue(pair.Key, out count))
                {
                    resultDict[pair.Key] = count + pair.Value;
                }
                else
                {
                    resultDict.Add(pair);
                }
            }
        }

        public string ApplyToFormula(string formula)
        {
            var resultMol = Molecule.FromDict(ApplyToMolecule(Molecule.ParseExpressionToDictionary(formula)));
            return resultMol.ToString();
        }

        public string ApplyIsotopeLabelsToFormula(string formula)
        {
            if (!HasIsotopeLabels)
            {
                return formula;
            }
            var molecule = Molecule.ParseExpressionToDictionary(formula);
            var resultDict = new Dictionary<string, int>();
            foreach (var pair in molecule)
            {
                KeyValuePair<string, int> isotope;
                if (IsotopeLabels != null && IsotopeLabels.TryGetValue(pair.Key, out isotope))
                {
                    // If label is "2Cl37" and molecule is CH4Cl5 then result is CH4Cl3Cl'2
                    var unlabelCount = pair.Value - isotope.Value;
                    if (unlabelCount > 0)
                    {
                        int existResult;
                        if (resultDict.TryGetValue(pair.Key, out existResult))
                        {
                            resultDict[pair.Key] = existResult + unlabelCount;
                        }
                        else
                        {
                            resultDict.Add(pair.Key, unlabelCount);
                        }
                    }
                    else if (unlabelCount < 0)
                    {
                        throw new InvalidOperationException(
                            string.Format(Resources.BioMassCalc_ApplyAdductToFormula_Adduct_description___0___calls_for_more_labeled__1__than_are_found_in_the_molecule,
                                this, pair.Key));
                    }
                    int exist;
                    if (resultDict.TryGetValue(isotope.Key, out exist))
                    {
                        resultDict[isotope.Key] = exist + isotope.Value;
                    }
                    else
                    {
                        resultDict.Add(isotope.Key, isotope.Value);
                    }
                }
                else
                {
                    int exist;
                    if (resultDict.TryGetValue(pair.Key, out exist))
                    {
                        resultDict[pair.Key] = exist + pair.Value;
                    }
                    else
                    {
                        resultDict.Add(pair.Key, pair.Value);
                    }
                }
            }
            var resultMol = Molecule.FromDict(resultDict);
            return resultMol.ToString();
        }

        public double ApplyIsotopeLabelsToMass(TypedMass mass)
        {
            // Account for the added mass of any labels delared in the adduct, e.g. for [2M4Cl37+H] add 2x4x the difference in mass between CL37 and Cl
            if (mass.IsHeavy())
            {
                return mass; // Mass already has isotope masses factored in
            }
            if (!HasIsotopeLabels)
            {
                return mass;
            }
            return (mass.IsMonoIsotopic() ? IsotopesIncrementalMonoMass : IsotopesIncrementalAverageMass) + mass; 
        }

        /// <summary>
        /// Returns the effect of the adduct on the input mass,
        /// including the mass multipler and any isotope labels if the mass isn't marked heavy (ie already has labels accounted for)
        /// </summary>
        public TypedMass ApplyToMass(TypedMass neutralMass)
        {
            var adductMass = neutralMass.IsHeavy() ? 
                neutralMass: // Mass already takes isotopes into account
                neutralMass.MassType.IsAverage() ? (IsotopesIncrementalAverageMass + AverageMassAdduct) : (IsotopesIncrementalMonoMass + MonoMassAdduct); // Mass of the Na and 2*3(mass C' - mass C) in [2M3C13+Na]
            Assume.IsTrue(adductMass.IsHeavy() == IsotopesIncrementalAverageMass.IsHeavy());
            return adductMass + neutralMass * MassMultiplier;
        }

        /// <summary>
        /// Get the mz when the adduct formula (including any mass multiplier and isotope labels) is applied to a neutral mass
        /// </summary>
        /// <param name="neutralMass">mass of a neutral molecule, and its mass tyoe </param>
        public double MzFromNeutralMass(TypedMass neutralMass)
        {
            return MzFromNeutralMass(neutralMass.Value, neutralMass.MassType);
        }

        /// <summary>
        /// Get the mz when the adduct formula (including any mass multiplier) is applied to a neutral mass
        /// </summary>
        /// <param name="neutralMass">mass of a neutral molecule</param>
        /// <param name="t">determines use of Average mass or Mono mass</param>
        public double MzFromNeutralMass(double neutralMass, MassType t)
        {
            if (neutralMass != 0 && t.IsMassH())
            {
                Assume.IsTrue(IsProtonated); // Expect massH to be a peptide thing only
                var iMass = t.IsAverage() ? IsotopesIncrementalAverageMass : IsotopesIncrementalMonoMass; // For example, mass of the 2*3*(cl37-Cl)in 2M3Cl37+2H
                return (iMass + neutralMass * MassMultiplier + (AdductCharge-1) * BioMassCalc.MassProton) / Math.Abs(AdductCharge);
            }
            // Treat protonation as a special case, so the numbers agree with how we traditionally deal with peptide charges
            if (IsProtonated)
            {
                var isotopeIncrementalMass = t.IsHeavy() ? 
                    0.0 : // Don't reapply isotope label mass
                    t.IsAverage() ? IsotopesIncrementalAverageMass : IsotopesIncrementalMonoMass; // For example, mass of the 2*3*(cl37-Cl)in 2M3Cl37+2H
                return (isotopeIncrementalMass + neutralMass * MassMultiplier + AdductCharge * BioMassCalc.MassProton) / Math.Abs(AdductCharge);
            }
            var adductMass = t.IsHeavy() ? // Don't reapply isotope label mass
                (t.IsAverage() ? AverageMassAdduct : MonoMassAdduct) : // For example, mass of the 2H in 2M3Cl37+2H
                (t.IsAverage() ? AverageMassAdduct + IsotopesIncrementalAverageMass : MonoMassAdduct + IsotopesIncrementalMonoMass); // For example, mass of the 2H and 2*3*(cl37-Cl)in 2M3Cl37+2H
            return (neutralMass * MassMultiplier + adductMass - AdductCharge * BioMassCalc.MassElectron) / Math.Abs(AdductCharge);  
        }

        /// <summary>
        /// Work back from mz to mass of molecule without adduct (but with isotopes if any), accounting for electron loss or gain, 
        /// and adduct multiplier 
        /// </summary>
        /// <param name="mz">mz of ion (molecule+adduct)</param>
        /// <param name="t">determines use of Average mass or Mono mass</param>
        /// <returns></returns>
        public TypedMass MassFromMz(double mz, MassType t)
        {
            if (IsProtonated)
            {
                // Treat this as a special case, so the numbers agree with how we deal with peptide charges
                return new TypedMass((mz * Math.Abs(AdductCharge) - AdductCharge * BioMassCalc.MassProton) / MassMultiplier, t);
            }
            var adductMass = t.IsAverage() ? AverageMassAdduct : MonoMassAdduct;
            return new TypedMass((mz * Math.Abs(AdductCharge) + AdductCharge * BioMassCalc.MassElectron - adductMass) / MassMultiplier, t);
        }

        private void InitializeAsCharge(int charge, ADDUCT_TYPE mode)
        {
            Description = null;
            AdductCharge = charge;
            var composition = new Dictionary<string, int>();
            MassMultiplier = 1;
            if ((mode != ADDUCT_TYPE.charge_only) && (AdductCharge != 0))
            {
                composition.Add("H", AdductCharge); // Not L10N
            }
            Composition = new ImmutableDictionary<string, int>(composition);
            InitializeMasses();
        }

        private void InitializeMasses()
        {
            AverageMassAdduct = BioMassCalc.AVERAGE.CalculateMassFromFormula(Composition); // The average mass of the +2Na in [2M4Cl37+2Na]
            MonoMassAdduct = BioMassCalc.MONOISOTOPIC.CalculateMassFromFormula(Composition); // The mono mass of the +2Na in [2M4Cl37+2Na]
            if (IsotopeLabelMass.HasValue)
            {
                IsotopesIncrementalAverageMass = new TypedMass(MassMultiplier * IsotopeLabelMass.Value, MassType.AverageHeavy);
                IsotopesIncrementalMonoMass= new TypedMass(MassMultiplier * IsotopeLabelMass.Value, MassType.MonoisotopicHeavy);
            }
            else if (IsotopeLabels != null)
            {
                double avg = 0;
                double mono = 0;
                foreach (var isotope in IsotopeLabels)
                {
                    // Account for the added mass of any labels delared in the adduct, e.g. for [2M4Cl37+H] add 2x4x the difference in mass between CL37 and Cl
                    var unlabel = isotope.Key;
                    var label = isotope.Value.Key;
                    var labelCount = isotope.Value.Value;
                    avg += labelCount*(BioMassCalc.AVERAGE.GetMass(label) - BioMassCalc.AVERAGE.GetMass(unlabel));
                    mono += labelCount*(BioMassCalc.MONOISOTOPIC.GetMass(label) - BioMassCalc.MONOISOTOPIC.GetMass(unlabel));
                }
                IsotopesIncrementalAverageMass = new TypedMass(MassMultiplier * avg, MassType.AverageHeavy);
                IsotopesIncrementalMonoMass = new TypedMass(MassMultiplier * mono, MassType.MonoisotopicHeavy);
            }
            else
            {
                IsotopesIncrementalAverageMass = TypedMass.ZERO_AVERAGE_MASSNEUTRAL;
                IsotopesIncrementalMonoMass = TypedMass.ZERO_MONO_MASSNEUTRAL;
            }
            Unlabeled = ChangeIsotopeLabels(string.Empty); // Useful for dealing with labels and mass-only small molecule declarations
            IsProtonated = Composition.Any() && Composition.All(pair => pair.Key == BioMassCalc.H || pair.Key == BioMassCalc.H2);
            IsProteomic = IsProtonated && string.IsNullOrEmpty(Description); 
        }

        // Used for checking that different descriptions (ie "[M+H]" vs "[M+H]+") have same ion effect
        public bool SameEffect(Adduct obj)
        {
            if (ReferenceEquals(null, obj)) return false;
            if (ReferenceEquals(this, obj)) return true;
            if (Equals(this, obj)) return true;

            if (!Equals(obj.AdductCharge, AdductCharge) || 
                !Equals(obj.Composition.Count, Composition.Count) || 
                !Equals(obj.MassMultiplier, MassMultiplier) ||
                !Equals(IsotopeLabels == null, obj.IsotopeLabels == null) ||
                (IsotopeLabels != null && obj.IsotopeLabels != null && !Equals(obj.IsotopeLabels.Count, IsotopeLabels.Count)))
                return false;
            foreach (var atom in Composition)
            {
                int otherCount;
                if (!obj.Composition.TryGetValue(atom.Key, out otherCount))
                    return false;
                if (!Equals(atom.Value, otherCount))
                    return false;
            }
            if (IsotopeLabels != null)
            {
                foreach (var label in IsotopeLabels)
                {
                    KeyValuePair<string, int> otherLabelCount;
                    if (obj.IsotopeLabels == null || !obj.IsotopeLabels.TryGetValue(label.Key, out otherLabelCount))
                        return false;
                    if (!Equals(label.Value.Value, otherLabelCount.Value))
                        return false;
                }
            }
            return true;
        }

        // We want comparisons to be on the same order as comparing ints, as when we used to just use integer charge instead of proper adducts
        private void SetHashCode()
        {
            _hashCode = (Description != null ? Description.GetHashCode() : 0);
            _hashCode = (_hashCode * 397) ^ AdductCharge.GetHashCode();
            foreach (var pair in Composition)
            {
                _hashCode = (_hashCode * 397) ^ pair.Key.GetHashCode();
                _hashCode = (_hashCode * 397) ^ pair.Value.GetHashCode();
            }
        }


        #region object overrides

        public override int GetHashCode()
        {
            return _hashCode;
        }

        public bool Equals(Adduct obj)
        {
            if (ReferenceEquals(null, obj)) return false;
            if (_hashCode != obj._hashCode) return false;
            var equal = CompareTo(obj) == 0;
            return equal; // For debugging convenience
        }

        public override bool Equals(object obj)
        {
            if (ReferenceEquals(null, obj)) return false;
            if (ReferenceEquals(this, obj)) return true;
            if (obj.GetType() != typeof (Adduct)) return false;
            return Equals((Adduct)obj);
        }

        public static int Compare(Adduct left, Adduct right)
        {
            if (left == null)
            {
                return right == null ? 0 : -1;
            }
            return left.CompareTo(right);
        }

        // Lots of operator overrides, so we don't have to change masses of Skyline code
        // where we formerly used charge as a proxy for protonation

        public static bool operator ==(Adduct left, Adduct right)
        {
            return Equals(left, right);
        }

        public static bool operator !=(Adduct left, Adduct right)
        {
            return !Equals(left, right);
        }

        public static bool operator <(Adduct left, Adduct right)
        {
            return Compare(left, right) < 0;
        }

        public static bool operator <=(Adduct left, Adduct right)
        {
            return Compare(left, right) <= 0;
        }

        public static bool operator >=(Adduct left, Adduct right)
        {
            return Compare(left, right) >= 0;
        }

        public static bool operator >(Adduct left, Adduct right)
        {
            return Compare(left, right) > 0;
        }

        public int CompareTo(object obj)
        {
            if (ReferenceEquals(null, obj)) return 1;
            if (ReferenceEquals(this, obj)) return 0;
            var that = (Adduct)obj;
            var comp = AdductCharge.CompareTo(that.AdductCharge);
            if (comp != 0)
            {
                return comp;
            }
            comp = string.Compare(Description, that.Description, StringComparison.Ordinal);
            if (comp != 0)
            {
                return comp;
            }
            comp =  Composition.Count.CompareTo(that.Composition.Count);
            if (comp != 0)
            {
                return comp;
            }
            foreach (var atomCount in Composition)
            {
                int otherVal;
                if (Composition.TryGetValue(atomCount.Key, out otherVal))
                {
                    comp = atomCount.Value.CompareTo(otherVal);
                    if (comp != 0)
                    {
                        return comp;
                    }
                }
                else
                {
                    return 1;
                }
            }
            return 0;
        }

        // Return the full "[M+H]" style declaration even if marked as proteomic (that is, even if we aren't carrying a text description)
        public string AsFormula()
        {
            if (!string.IsNullOrEmpty(Description))
            {
                return Description;
            }
            Assume.IsFalse(HasIsotopeLabels); // For peptides we don't normally handle isotopes in the adduct
            return Composition.Aggregate("[M", (current, atom) => current + (atom.Value==1 ? "+" : atom.Value==-1 ? "-" : string.Format("{0:+#;-#;#}", atom.Value)) + atom.Key)+"]"; // Not L10N
        }

        // For protonation, return something like "+2" or "-3", for others the full "[M+Na]" style declaration
        public string AsFormulaOrSignedInt()
        {
            return Description ?? string.Format("{0:+#;-#;#}",AdductCharge); // Formatter for pos;neg;zero // Not L10N
        }

        // For protonation, return something like "2" or "-3", for others the full "[M+Na]" style declaration
        public string AsFormulaOrInt(CultureInfo culture = null)
        {
            return Description ?? AdductCharge.ToString(culture ?? CultureInfo.InvariantCulture); 
        }

        // For protonation, return something like "++" or "---", for others the full "[M+Na]" style declaration
        private static string plusses = "++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++"; // Not L10N
        private static string minuses = "------------------------------------------------------------------------------------------"; // Not L10N

        public string AsFormulaOrSigns()
        {
            if (!String.IsNullOrEmpty(Description))
            {
                // For charge-only adducts, fall through to show "+++" instead of showing "[M+3]"
                if (!IsChargeOnly)
                    return Description;
            }
            return (AdductCharge > 0) ? plusses.Substring(0, AdductCharge) : minuses.Substring(0, -AdductCharge);
        }

        public string ToString(CultureInfo culture)
        {
            return AsFormulaOrInt(culture);            
        }

        // For protonation, return something like "2" or "-3", for others the full "[M+Na]" style declaration
        public override string ToString()
        {
            return AsFormulaOrInt(CultureInfo.InvariantCulture);
        }

        #endregion
    }
}