// Copyright (C) 2012 Zeno Gantner
//
// This file is part of MyMediaLite.
//
// MyMediaLite is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// MyMediaLite is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.
//
//  You should have received a copy of the GNU General Public License
//  along with MyMediaLite.  If not, see <http://www.gnu.org/licenses/>.
//

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using MyMediaLite.DataType;
using MyMediaLite.IO;

namespace MyMediaLite.RatingPrediction
{
	/// <summary>SVD++: Matrix factorization that also takes into account _what_ users have rated; variant that uses a sigmoid function</summary>
	/// <remarks>
	///   <para>
	///     Literature:
	///     <list type="bullet">
	///       <item><description>
	///         Yehuda Koren:
	///         Factorization Meets the Neighborhood: a Multifaceted Collaborative Filtering Model.
	///         KDD 2008.
	///         http://research.yahoo.com/files/kdd08koren.pdf
	///       </description></item>
	///     </list>
	///   </para>
	/// </remarks>
	public class SigmoidSVDPlusPlus : MatrixFactorization
	{
		// TODO
		// - implement ComputeObjective
		// - handle fold-in

		float[] user_bias;
		float[] item_bias;
		Matrix<float> y;
		Matrix<float> p;

		// TODO update this structure on incremental updates
		int[][] items_rated_by_user;

		/// <summary>size of the interval of valid ratings</summary>
		double rating_range_size;

		delegate float TwoDoubleToFloat(double arg1, double arg2);
		TwoDoubleToFloat compute_gradient_common;

		/// <summary>The optimization target</summary>
		public OptimizationTarget Loss { get; set; }

		/// <summary>bias learn rate</summary>
		public float BiasLearnRate { get; set; }
		/// <summary>regularization constant for biases</summary>
		public float BiasReg { get; set; }

		/// <summary>Default constructor</summary>
		public SigmoidSVDPlusPlus() : base()
		{
			Regularization = 0.015f;
			LearnRate = 0.001f;
			BiasLearnRate = 0.007f;
			BiasReg = 0.005f;
		}

		///
		public override void Train()
		{
			items_rated_by_user = new int[MaxUserID + 1][];
			for (int u = 0; u <= MaxUserID; u++)
				items_rated_by_user[u] = (from index in ratings.ByUser[u] select ratings.Items[index]).ToArray();

			rating_range_size = max_rating - min_rating;

			// compute global bias
			double avg = (ratings.Average - min_rating) / rating_range_size;
			global_bias = (float) Math.Log(avg / (1 - avg));

			switch (Loss)
			{
				case OptimizationTarget.MAE:
					compute_gradient_common = (sig_score, err) => (float) (Math.Sign(err) * sig_score * (1 - sig_score) * rating_range_size);
					break;
				case OptimizationTarget.RMSE:
					compute_gradient_common = (sig_score, err) => (float) (err * sig_score * (1 - sig_score) * rating_range_size);
					break;
				case OptimizationTarget.LogisticLoss:
					compute_gradient_common = (sig_score, err) => (float) err;
					break;
			}

			base.Train();
		}

		///
		public override float Predict(int user_id, int item_id)
		{
			double score = global_bias;

			if (user_id <= MaxUserID)
				score += user_bias[user_id];
			if (item_id <= MaxItemID)
				score += item_bias[item_id];
			if (user_id <= MaxUserID && item_id <= MaxItemID)
				score += MatrixExtensions.RowScalarProduct(user_factors, user_id, item_factors, item_id);

			double sig_score = 1 / (1 + Math.Exp(-score));
			double prediction = min_rating + sig_score * rating_range_size;

			return (float) prediction;
		}

		///
		protected override void InitModel()
		{
			base.InitModel();

			p = new Matrix<float>(MaxUserID + 1, NumFactors);
			p.InitNormal(InitMean, InitStdDev);
			y = new Matrix<float>(MaxItemID + 1, NumFactors);
			y.InitNormal(InitMean, InitStdDev);

			user_bias = new float[MaxUserID + 1];
			item_bias = new float[MaxItemID + 1];
		}

		///
		protected override void Iterate(IList<int> rating_indices, bool update_user, bool update_item)
		{
			float reg = Regularization; // to limit property accesses
			float lr  = LearnRate;

			foreach (int index in rating_indices)
			{
				int u = ratings.Users[index];
				int i = ratings.Items[index];

				double score = global_bias + user_bias[u] + item_bias[i];
				var u_plus_y_sum_vector = y.SumOfRows(items_rated_by_user[u]);
				double norm_denominator = Math.Sqrt(ratings.CountByUser[u]);
				for (int f = 0; f < u_plus_y_sum_vector.Count; f++)
					u_plus_y_sum_vector[f] = (float) (u_plus_y_sum_vector[f] / norm_denominator + p[u, f]);

				score += MatrixExtensions.RowScalarProduct(item_factors, i, u_plus_y_sum_vector);
				double sig_score = 1 / (1 + Math.Exp(-score));

				double prediction = min_rating + sig_score * rating_range_size;
				double err = ratings[index] - prediction;

				float gradient_common = compute_gradient_common(sig_score, err);

				// adjust biases
				if (update_user)
					user_bias[u] += BiasLearnRate * (gradient_common - BiasReg * user_bias[u]);
				if (update_item)
					item_bias[i] += BiasLearnRate * (gradient_common - BiasReg * item_bias[i]);

				// adjust factors
				double x = gradient_common / norm_denominator; // TODO better name than x
				for (int f = 0; f < NumFactors; f++)
				{
					float i_f = item_factors[i, f];

					// if necessary, compute and apply updates
					if (update_user)
					{
						double delta_u = gradient_common * i_f - reg * p[u, f];
						p.Inc(u, f, lr * delta_u);
					}
					if (update_item)
					{
						double delta_i = gradient_common * u_plus_y_sum_vector[f] - reg * i_f;
						item_factors.Inc(i, f, lr * delta_i);

						double common_update = x * i_f;
						foreach (int other_item_id in items_rated_by_user[u])
						{
							double delta_oi = common_update - reg * y[other_item_id, f];
							y.Inc(other_item_id, f, lr * delta_oi);
						}
					}
				}
			}

			// pre-compute complete user factors
			for (int u = 0; u <= MaxUserID; u++)
				PrecomputeFactors(u);
		}

		void PrecomputeFactors(int u)
		{
			// compute
			var factors = y.SumOfRows(items_rated_by_user[u]);
			double norm_denominator = Math.Sqrt(ratings.CountByUser[u]);
			for (int f = 0; f < factors.Count; f++)
				factors[f] = (float) (factors[f] / norm_denominator + p[u, f]);

			// assign
			for (int f = 0; f < factors.Count; f++)
				user_factors[u, f] = (float) factors[f];
		}

		///
		protected override void AddUser(int user_id)
		{
			base.AddUser(user_id);
			Array.Resize(ref user_bias, MaxUserID + 1);
		}

		///
		protected override void AddItem(int item_id)
		{
			base.AddItem(item_id);
			Array.Resize(ref item_bias, MaxItemID + 1);
		}

		/// <summary>Updates the latent factors on a user</summary>
		/// <param name="user_id">the user ID</param>
		public override void RetrainUser(int user_id)
		{
			if (UpdateUsers)
			{
				base.RetrainUser(user_id);
				PrecomputeFactors(user_id);
			}
		}

		///
		public override void SaveModel(string filename)
		{
			using ( StreamWriter writer = Model.GetWriter(filename, this.GetType(), "2.99") )
			{
				writer.WriteLine(global_bias.ToString(CultureInfo.InvariantCulture));
				writer.WriteVector(user_bias);
				writer.WriteVector(item_bias);
				writer.WriteMatrix(p);
				writer.WriteMatrix(y);
				writer.WriteMatrix(item_factors);
			}
		}

		///
		public override void LoadModel(string filename)
		{
			using ( StreamReader reader = Model.GetReader(filename, this.GetType()) )
			{
				var global_bias = float.Parse(reader.ReadLine(), CultureInfo.InvariantCulture);
				var user_bias = reader.ReadVector();
				var item_bias = reader.ReadVector();
				var p            = (Matrix<float>) reader.ReadMatrix(new Matrix<float>(0, 0));
				var y            = (Matrix<float>) reader.ReadMatrix(new Matrix<float>(0, 0));
				var item_factors = (Matrix<float>) reader.ReadMatrix(new Matrix<float>(0, 0));

				if (user_bias.Count != p.dim1)
					throw new IOException(
						string.Format(
							"Number of users must be the same for biases and factors: {0} != {1}",
							user_bias.Count, p.dim1));
				if (item_bias.Count != item_factors.dim1)
					throw new IOException(
						string.Format(
							"Number of items must be the same for biases and factors: {0} != {1}",
							item_bias.Count, item_factors.dim1));

				if (p.NumberOfColumns != item_factors.NumberOfColumns)
					throw new Exception(
						string.Format("Number of user (p) and item factors must match: {0} != {1}",
							p.NumberOfColumns, item_factors.NumberOfColumns));
				if (y.NumberOfColumns != item_factors.NumberOfColumns)
					throw new Exception(
						string.Format("Number of user (y) and item factors must match: {0} != {1}",
							y.NumberOfColumns, item_factors.NumberOfColumns));

				this.MaxUserID = p.NumberOfRows - 1;
				this.MaxItemID = item_factors.NumberOfRows - 1;

				// assign new model
				this.global_bias = global_bias;
				if (this.NumFactors != item_factors.NumberOfColumns)
				{
					Console.Error.WriteLine("Set NumFactors to {0}", item_factors.NumberOfColumns);
					this.NumFactors = (uint) item_factors.NumberOfColumns;
				}
				this.user_bias = user_bias.ToArray();
				this.item_bias = item_bias.ToArray();
				this.p = p;
				this.y = y;
				this.item_factors = item_factors;

				for (int u = 0; u <= MaxUserID; u++)
					PrecomputeFactors(u);
			}
		}

		///
		public override string ToString()
		{
			return string.Format(
				CultureInfo.InvariantCulture,
				"{0} num_factors={1} regularization={2} bias_reg={3} learn_rate={4} bias_learn_rate={5} num_iter={6} loss={7} init_mean={8} init_stddev={9}",
				this.GetType().Name, NumFactors, Regularization, BiasReg, LearnRate,  BiasLearnRate, NumIter, Loss, InitMean, InitStdDev);
		}
	}
}
