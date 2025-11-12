namespace InvestmentPortfolioSimulation
{
    class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine("Investment Portfolio Monte Carlo Simulation");
            Console.WriteLine("===========================================\n");

            var simulator = new PortfolioSimulator();
            simulator.RunSimulation(
                numSimulations: 40000,
                years: 40,
                initialInvestment: 2500000
            );

            Console.WriteLine("\nPress any key to exit...");
            Console.ReadKey();
        }
    }

    public class PortfolioSimulator
    {
        private readonly Random _random = new Random(42); // Seed for reproducibility

        // Asset class parameters (real returns, annualized)
        private readonly double equityMean = 0.08;
        private readonly double equityStdDev = 0.18;
        private readonly double bondMean = 0.03;
        private readonly double bondStdDev = 0.06;
        private readonly double propertyMean = 0.05;
        private readonly double propertyStdDev = 0.12;
        private readonly double cgtRebalancingDrag = 0.005; // 0.5% annual drag from realised gains

        // Correlation matrix
        // Equity-Bond: -0.2 (slight negative, flight to safety)
        // Equity-Property: 0.6 (positive correlation)
        // Bond-Property: 0.1 (low correlation)
        private readonly double[,] correlationMatrix = new double[,]
        {
            { 1.0, -0.2, 0.6 },  // Equity correlations
            { -0.2, 1.0, 0.1 },  // Bond correlations
            { 0.6, 0.1, 1.0 }    // Property correlations
        };

        // Portfolio allocations
        private readonly double[] yourPortfolio = { 1.0, 0.0, 0.0 }; // 100% equity
        private readonly double[] alPortfolio = { 0.78, 0.146, 0.064 }; // equities, bonds, alternatives

        private readonly double yourFees = 0.0015; // 0.15%
        private readonly double alFees = 0.013; // 1.3%

        int annualWithdrawalStart = 9;

        double housePrice = 2500000;
        double mortgagePayment = 40000;
        double mortgageRelease = 1300000;

        int startYear = 2023;
        int bjrBirthYear = 1971;
        int mortgageYearStart = 2027;

        double annualWithdrawal = 190000; // starting draw
        bool inflationLinked = true;
        double inflationRate = 0.028; // 2.8% inflation

        public void RunSimulation(int numSimulations, int years, double initialInvestment)
        {
            var choleskyMatrix = CholeskyDecomposition(correlationMatrix);
            var yourResults = new List<SimulationResult>();
            var alResults = new List<SimulationResult>();

            Console.WriteLine($"Running {numSimulations:N0} simulations over {years} years...\n");

            for (int sim = 0; sim < numSimulations; sim++)
            {
                if (sim % 1000 == 0)
                    Console.Write($"\rProgress: {sim}/{numSimulations}");

                var yourResult = SimulatePortfolio(
                    initialInvestment,
                    years,
                    yourPortfolio,
                    yourFees,
                    choleskyMatrix
                );

                var alResult = SimulatePortfolio(
                    initialInvestment,
                    years,
                    alPortfolio,
                    alFees,
                    choleskyMatrix
                );

                yourResults.Add(yourResult);
                alResults.Add(alResult);
            }

            Console.WriteLine($"\rProgress: {numSimulations}/{numSimulations} - Complete!\n");

            PrintResults("Your 100% Equity Portfolio (0.15% fees)", yourResults, years);
            Console.WriteLine();
            PrintResults("Arbuthnot Latham Risk 6/7 (1.3% fees)", alResults, years);
            Console.WriteLine();
            PrintComparison(yourResults, alResults, initialInvestment);
        }

        private SimulationResult SimulatePortfolio(
            double initialValue,
            int years,
            double[] allocation,
            double fees,
            double[,] choleskyMatrix)
        {
            // Initialize cash buffers and portfolio
            double cashBuffer = 0;                           // dynamic skim buffer
            double permanentCash = initialValue * 0.05;      // 5% permanent buffer

            // Initialize wrappers (95% of initial investment split across types)
            double investableAmount = initialValue * 0.95;
            double taxable = investableAmount * 0.84;
            double isa = investableAmount * 0.05;
            double pension = investableAmount * 0.11;

            // State tracking
            double portfolioValue = taxable + isa + pension;  // SINGLE SOURCE OF TRUTH
            double prevPeak = portfolioValue;                 // For skim calculation
            double peakValue = initialValue;                  // For drawdown tracking

            double maxValue = initialValue;
            double maxDrawdown = 0;
            int yearsInDrawdown = 0;
            int currentDrawdownYears = 0;
            int cutYears = 0;

            // Tax rates for each wrapper type
            double taxRateTaxable = 0.24;
            double taxRateISA = 0.0;
            double taxRatePension = 0.30;
            double totalTaxPaid = 0;

            var yearlyReturns = new List<double>();

            for (int year = 0; year < years; year++)
            {
                // ================================================================
                // PHASE 1: CALCULATE BASE WITHDRAWAL AMOUNT
                // ================================================================
                double currentWithdrawal = annualWithdrawal;

                int bjrAge = (startYear + year) - bjrBirthYear;

                // Age-based reduction in spending needs
                if (bjrAge >= 75)
                    currentWithdrawal *= 0.729; // 0.9^3
                else if (bjrAge >= 70)
                    currentWithdrawal *= 0.81; // 0.9^2
                else if (bjrAge >= 65)
                    currentWithdrawal *= 0.9; // 0.9^1

                // 100k injection in 2024
                if ((startYear + year) == 2024)
                {
                    taxable += 100000;
                    portfolioValue += 100000;
                    prevPeak = portfolioValue;
                }

                // Year 21: house downsize adds £1,700,000 to taxable account
                if (bjrAge == 75)
                {
                    taxable += mortgageRelease;
                    portfolioValue += mortgageRelease;
                    prevPeak = portfolioValue;
                }

                // ================================================================
                // PHASE 2: GENERATE RETURNS AND UPDATE PORTFOLIO
                // ================================================================

                // Generate correlated asset returns
                var assetReturns = GenerateCorrelatedReturns(choleskyMatrix);

                // Calculate portfolio return based on allocation
                double portfolioReturn = 0;
                for (int i = 0; i < allocation.Length; i++)
                {
                    portfolioReturn += allocation[i] * assetReturns[i];
                }

                // Apply fees
                portfolioReturn -= fees;

                // Apply CGT/rebalancing drag to taxable wrapper only (in positive return years)
                if (portfolioReturn > 0)
                {
                    double totalWrappers = taxable + isa + pension;
                    double taxableWeight = (totalWrappers > 0) ? taxable / totalWrappers : 0;
                    double cgtDrag = cgtRebalancingDrag * taxableWeight;
                    portfolioReturn -= cgtDrag;
                }

                yearlyReturns.Add(portfolioReturn);

                // Update portfolio value after returns - apply to each wrapper
                taxable *= (1 + portfolioReturn);
                isa *= (1 + portfolioReturn);
                pension *= (1 + portfolioReturn);
                portfolioValue = taxable + isa + pension;

                // ================================================================
                // PHASE 3: SKIM EXCESS GAINS (Dynamic Cash Buffer Management)
                // ================================================================

                // When portfolio exceeds previous peak by 15%, skim down to 110% of peak
                if (portfolioValue > prevPeak * 1.15)
                {
                    double skim = portfolioValue - prevPeak * 1.10;
                    double skimRatio = skim / portfolioValue;

                    // Reduce each wrapper proportionally
                    taxable -= taxable * skimRatio;
                    isa -= isa * skimRatio;
                    pension -= pension * skimRatio;

                    portfolioValue = taxable + isa + pension;
                    cashBuffer += skim;
                    prevPeak = portfolioValue;
                }

                // ================================================================
                // PHASE 4: REBUILD PERMANENT CASH BUFFER (if needed)
                // ================================================================

                // In strong recovery years (>8% return), rebuild permanent cash to 5% of total wealth
                double totalWealth = portfolioValue + permanentCash + cashBuffer;
                double targetPermanentCash = totalWealth * 0.05;

                if (portfolioReturn > 0.08 && permanentCash < targetPermanentCash)
                {
                    double topUp = Math.Min(targetPermanentCash - permanentCash,
                                            portfolioValue * 0.02);  // Max 2% of portfolio per year
                    double topUpRatio = topUp / portfolioValue;

                    // Reduce each wrapper proportionally
                    taxable -= taxable * topUpRatio;
                    isa -= isa * topUpRatio;
                    pension -= pension * topUpRatio;

                    portfolioValue = taxable + isa + pension;
                    permanentCash += topUp;
                }

                // ================================================================
                // PHASE 5: PROCESS WITHDRAWAL (SINGLE UNIFIED LOGIC)
                // ================================================================

                if (year >= annualWithdrawalStart)
                {
                    // Calculate inflation-adjusted withdrawal
                    double withdrawal = currentWithdrawal;
                    if (inflationLinked)
                    {
                        withdrawal *= Math.Pow(1 + inflationRate, year);
                    }

                    // Apply 10% cut in down years
                    double effectiveWithdrawal = withdrawal;
                    if (portfolioReturn < 0)
                    {
                        effectiveWithdrawal *= 0.9;
                        cutYears++;
                    }

                    // Add mortgage payment (years 5-22)
                    effectiveWithdrawal += (startYear + year) >= 2026 && (startYear + year) <= 2046 ? mortgagePayment : 0;

                    // This is what we actually need to spend (net of taxes)
                    double netNeed = effectiveWithdrawal;
                    double grossWithdrawal = 0;  // Total withdrawn from accounts (before taxes)
                    double thisYearTax = 0;      // Taxes paid this year

                    // ---------------------------------------------------------------
                    // Step 1: Use cash buffers first (tax-free, no further deduction needed)
                    // ---------------------------------------------------------------
                    double totalAvailableCash = permanentCash + cashBuffer;
                    double fromCash = Math.Min(netNeed, totalAvailableCash);

                    if (fromCash > 0)
                    {
                        // Withdraw from permanent cash first, then dynamic buffer
                        double fromPermanent = Math.Min(fromCash, permanentCash);
                        permanentCash -= fromPermanent;

                        double fromDynamic = fromCash - fromPermanent;
                        cashBuffer -= fromDynamic;

                        netNeed -= fromCash;  // Reduce remaining need
                    }

                    // ---------------------------------------------------------------
                    // Step 2: If cash insufficient, withdraw from investment wrappers
                    // ---------------------------------------------------------------
                    if (netNeed > 0)
                    {
                        // Try to withdraw from wrappers in order: Taxable -> ISA -> Pension
                        // Each has different tax treatment

                        // Option A: Withdraw fully from Taxable account (24% tax)
                        if (taxable >= netNeed / (1 - taxRateTaxable))
                        {
                            grossWithdrawal = netNeed / (1 - taxRateTaxable);
                            taxable -= grossWithdrawal;
                            thisYearTax = grossWithdrawal - netNeed;
                        }
                        // Option B: Taxable insufficient, use ISA (0% tax)
                        else if (isa >= netNeed)
                        {
                            grossWithdrawal = netNeed;  // No tax on ISA
                            isa -= grossWithdrawal;
                        }
                        // Option C: ISA insufficient, use Pension (30% tax)
                        else if (pension >= netNeed / (1 - taxRatePension))
                        {
                            grossWithdrawal = netNeed / (1 - taxRatePension);
                            pension -= grossWithdrawal;
                            thisYearTax = grossWithdrawal - netNeed;
                        }
                        // Option D: All individual wrappers insufficient, withdraw proportionally
                        else
                        {
                            double remaining = taxable + isa + pension;
                            if (remaining > 0)
                            {
                                double ratio = Math.Min(netNeed / remaining, 1.0);
                                taxable *= (1 - ratio);
                                isa *= (1 - ratio);
                                pension *= (1 - ratio);
                                grossWithdrawal = remaining * ratio;

                                // For proportional withdrawal, approximate tax as weighted average
                                // (this is conservative; actual tax would be slightly more complex)
                            }
                        }

                        portfolioValue = taxable + isa + pension;  // Update portfolio value
                        totalTaxPaid += thisYearTax;
                    }

                    // ---------------------------------------------------------------
                    // Step 3: Check for portfolio depletion
                    // ---------------------------------------------------------------
                    if (portfolioValue <= 0 && permanentCash <= 0 && cashBuffer <= 0)
                    {
                        // Portfolio ran out - return early with year of depletion
                        return new SimulationResult
                        {
                            StartValue = initialValue,
                            StartYear = annualWithdrawalStart,
                            StartDraw = annualWithdrawal,
                            HousePrice = housePrice,
                            FinalValue = 0,
                            MaxDrawdown = 1.0, // 100% drawdown
                            YearsInDrawdown = year + 1,
                            SharpeRatio = 0,
                            AnnualizedReturn = -1,
                            YearsSurvived = year + 1,
                            CutYears = cutYears,
                            FinalPermanentCash = 0,
                            FinalDynamicCash = 0,
                            TotalTaxPaid = totalTaxPaid
                        };
                    }
                }

                // ================================================================
                // PHASE 6: TRACK DRAWDOWN METRICS
                // ================================================================

                double currentTotalWealth = portfolioValue + permanentCash + cashBuffer;

                if (currentTotalWealth > peakValue)
                {
                    peakValue = currentTotalWealth;
                    if (currentDrawdownYears > 0)
                    {
                        yearsInDrawdown += currentDrawdownYears;
                        currentDrawdownYears = 0;
                    }
                }
                else
                {
                    currentDrawdownYears++;
                    double currentDrawdown = (peakValue - currentTotalWealth) / peakValue;
                    maxDrawdown = Math.Max(maxDrawdown, currentDrawdown);
                }

                maxValue = Math.Max(maxValue, currentTotalWealth);

            }

            // ================================================================
            // FINAL RESULTS: Portfolio survived all years
            // ================================================================

            double finalTotalWealth = portfolioValue + permanentCash + cashBuffer;
            double avgReturn = yearlyReturns.Average();
            double stdDev = Math.Sqrt(yearlyReturns.Select(r => Math.Pow(r - avgReturn, 2)).Average());
            double sharpeRatio = (avgReturn - 0.02) / stdDev;

            return new SimulationResult
            {
                StartValue = initialValue,
                StartYear = annualWithdrawalStart,
                StartDraw = annualWithdrawal,
                HousePrice = housePrice,
                FinalValue = finalTotalWealth,
                MaxDrawdown = maxDrawdown,
                YearsInDrawdown = yearsInDrawdown + currentDrawdownYears,
                SharpeRatio = sharpeRatio,
                AnnualizedReturn = Math.Pow(finalTotalWealth / initialValue, 1.0 / years) - 1,
                YearsSurvived = years,
                CutYears = cutYears,
                FinalPermanentCash = permanentCash,
                FinalDynamicCash = cashBuffer,
                TotalTaxPaid = totalTaxPaid,
            };
        }

        private double[] GenerateCorrelatedReturns(double[,] choleskyMatrix)
        {
            // Generate independent standard normal variables
            double[] independent = new double[3];
            for (int i = 0; i < 3; i++)
            {
                independent[i] = BoxMullerTransform();
            }

            // Apply Cholesky transformation for correlation
            double[] correlated = new double[3];
            for (int i = 0; i < 3; i++)
            {
                correlated[i] = 0;
                for (int j = 0; j <= i; j++)
                {
                    correlated[i] += choleskyMatrix[i, j] * independent[j];
                }
            }

            // Convert to actual returns
            double[] returns = new double[3];
            returns[0] = equityMean + correlated[0] * equityStdDev;
            returns[1] = bondMean + correlated[1] * bondStdDev;
            returns[2] = propertyMean + correlated[2] * propertyStdDev;

            return returns;
        }

        private double BoxMullerTransform()
        {
            double u1 = 1.0 - _random.NextDouble();
            double u2 = 1.0 - _random.NextDouble();
            return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Sin(2.0 * Math.PI * u2);
        }

        private double[,] CholeskyDecomposition(double[,] matrix)
        {
            int n = 3;
            double[,] L = new double[n, n];

            for (int i = 0; i < n; i++)
            {
                for (int j = 0; j <= i; j++)
                {
                    double sum = 0;
                    for (int k = 0; k < j; k++)
                    {
                        sum += L[i, k] * L[j, k];
                    }

                    if (i == j)
                    {
                        L[i, j] = Math.Sqrt(matrix[i, i] - sum);
                    }
                    else
                    {
                        L[i, j] = (matrix[i, j] - sum) / L[j, j];
                    }
                }
            }

            return L;
        }

        private void PrintResults(string portfolioName, List<SimulationResult> results, int targetYears)
        {
            Console.WriteLine($"{portfolioName}");
            Console.WriteLine(new string('-', portfolioName.Length));

            var finalValues = results.Select(r => r.FinalValue).OrderBy(v => v).ToList();
            var drawdowns = results.Select(r => r.MaxDrawdown).ToList();
            var sharpeRatios = results.Select(r => r.SharpeRatio).ToList();
            var annualizedReturns = results.Select(r => r.AnnualizedReturn).ToList();
            var yearsSurvived = results.Select(r => r.YearsSurvived).ToList();

            // Calculate ruin probability
            int depleted = results.Count(r => r.YearsSurvived < targetYears);
            double ruinProbability = (double)depleted / results.Count;

            Console.WriteLine($"Assumptions:");
            Console.WriteLine($"  Start value:    £{results.Select(r => r.StartValue).First()}");
            Console.WriteLine($"  Start year:    £{results.Select(r => r.StartYear).First()}");
            Console.WriteLine($"  Start draw:    £{results.Select(r => r.StartDraw).First()}");
            Console.WriteLine($"  House price:    £{results.Select(r => r.HousePrice).First()}");

            Console.WriteLine($"Terminal Value:");
            Console.WriteLine($"  Median:    £{GetPercentile(finalValues, 0.5):N0}");
            Console.WriteLine($"  10th %ile: £{GetPercentile(finalValues, 0.1):N0}");
            Console.WriteLine($"  90th %ile: £{GetPercentile(finalValues, 0.9):N0}");
            Console.WriteLine($"  Mean:      £{finalValues.Average():N0}");

            Console.WriteLine($"\nSurvival Analysis:");
            Console.WriteLine($"  Probability of ruin (before year {targetYears}): {ruinProbability:P2}");
            Console.WriteLine($"  Simulations depleted: {depleted:N0} of {results.Count:N0}");

            if (depleted > 0)
            {
                var depletedYears = results.Where(r => r.YearsSurvived < targetYears)
                    .Select(r => (double)r.YearsSurvived)
                    .OrderBy(y => y)
                    .ToList();
                Console.WriteLine($"  Median year of depletion (if depleted): Year {GetPercentile(depletedYears, 0.5):F0}");
            }

            int preDownsizeFails = results.Count(r => r.YearsSurvived < 21);
            double preDownsizeRuin = (double)preDownsizeFails / results.Count;
            Console.WriteLine($"  Probability of ruin before house downsize (Year 21): {preDownsizeRuin:P2}");
            Console.WriteLine($"  Avg. years with spending cut: {results.Average(r => r.CutYears):F1}");

            // Cash buffer statistics
            var survivedResults = results.Where(r => r.YearsSurvived >= targetYears).ToList();
            if (survivedResults.Any())
            {
                Console.WriteLine($"\nCash Buffer Analysis (survivors only):");
                Console.WriteLine($"  Avg final permanent cash: £{survivedResults.Average(r => r.FinalPermanentCash):N0}");
                Console.WriteLine($"  Avg final dynamic buffer: £{survivedResults.Average(r => r.FinalDynamicCash):N0}");
            }

            Console.WriteLine($"\nAnnualized Return:");
            Console.WriteLine($"  Median: {GetPercentile(annualizedReturns, 0.5):P2}");
            Console.WriteLine($"  Mean:   {annualizedReturns.Average():P2}");

            Console.WriteLine($"\nMaximum Drawdown:");
            Console.WriteLine($"  Median:      {GetPercentile(drawdowns, 0.5):P1}");
            Console.WriteLine($"  Worst (95th): {GetPercentile(drawdowns, 0.95):P1}");
            Console.WriteLine($"  Mean:        {drawdowns.Average():P1}");

            Console.WriteLine($"\nAvg Years in Drawdown: {results.Average(r => r.YearsInDrawdown):F1}");
            Console.WriteLine($"Sharpe Ratio (mean): {sharpeRatios.Average():F2}");

            Console.WriteLine($"\nTax Impact:");
            Console.WriteLine($"  Avg lifetime tax paid: £{results.Average(r => r.TotalTaxPaid):N0}");
            Console.WriteLine($"  Avg annual tax: £{results.Average(r => r.TotalTaxPaid / targetYears):N0}");
        }

        private void PrintComparison(List<SimulationResult> yourResults, List<SimulationResult> alResults, double initial)
        {
            Console.WriteLine("Comparative Analysis");
            Console.WriteLine("====================");

            int yourWins = 0;
            double totalOutperformance = 0;

            for (int i = 0; i < yourResults.Count; i++)
            {
                if (yourResults[i].FinalValue > alResults[i].FinalValue)
                {
                    yourWins++;
                }
                totalOutperformance += (yourResults[i].FinalValue - alResults[i].FinalValue);
            }

            double yourMedian = GetPercentile(yourResults.Select(r => r.FinalValue).OrderBy(v => v).ToList(), 0.5);
            double alMedian = GetPercentile(alResults.Select(r => r.FinalValue).OrderBy(v => v).ToList(), 0.5);

            Console.WriteLine($"Your portfolio outperforms in {yourWins:N0} of {yourResults.Count:N0} simulations ({(double)yourWins / yourResults.Count:P1})");
            Console.WriteLine($"\nMedian terminal value difference: £{yourMedian - alMedian:N0}");
            Console.WriteLine($"Mean terminal value difference:   £{totalOutperformance / yourResults.Count:N0}");

            double yourVolatility = Math.Sqrt(yourResults.Select(r => r.FinalValue).Select(v => Math.Pow(v - yourResults.Average(r => r.FinalValue), 2)).Average());
            double alVolatility = Math.Sqrt(alResults.Select(r => r.FinalValue).Select(v => Math.Pow(v - alResults.Average(r => r.FinalValue), 2)).Average());

            Console.WriteLine($"\nTerminal value volatility:");
            Console.WriteLine($"  Your portfolio: £{yourVolatility:N0}");
            Console.WriteLine($"  AL portfolio:   £{alVolatility:N0}");
            Console.WriteLine($"  Difference:     {(1 - alVolatility / yourVolatility):P1} lower for AL");

            Console.WriteLine($"\nFee impact over 40 years:");
            double feeImpact = (yourMedian - alMedian) / yourMedian;
            Console.WriteLine($"  The 1.1% fee difference costs approximately {feeImpact:P1} of terminal wealth");
        }

        private double GetPercentile(List<double> sortedList, double percentile)
        {
            int index = (int)(percentile * sortedList.Count);
            if (index >= sortedList.Count) index = sortedList.Count - 1;
            return sortedList[index];
        }
    }

    public class SimulationResult
    {
        public double StartValue { get; set; }
        public double StartYear { get; set; }
        public double StartDraw { get; set; }
        public double HousePrice { get; set; }
        public double FinalValue { get; set; }
        public double MaxDrawdown { get; set; }
        public int YearsInDrawdown { get; set; }
        public double SharpeRatio { get; set; }
        public double AnnualizedReturn { get; set; }
        public int YearsSurvived { get; set; }
        public int CutYears { get; set; }
        public double FinalPermanentCash { get; set; }
        public double FinalDynamicCash { get; set; }
        public double TotalTaxPaid { get; set; }
    }
}
