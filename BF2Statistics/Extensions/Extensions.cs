﻿using System;
using System.Text;

namespace BF2Statistics
{
    static class RandomExtensions
    {
        /// <summary>
        /// Shuffles the current List or array
        /// </summary>
        /// <see cref="http://stackoverflow.com/a/110570/841267"/>
        public static void Shuffle<T>(this Random rng, T[] array)
        {
            int n = array.Length;
            while (n > 1)
            {
                int k = rng.Next(n--);
                T temp = array[n];
                array[n] = array[k];
                array[k] = temp;
            }
        }
    }

    public static class StringExtensions
    {
        public static string Repeat(this string input, int count)
        {
            StringBuilder builder = new StringBuilder((input == null ? 0 : input.Length) * count);

            for (int i = 0; i < count; i++) 
                builder.Append(input);

            return builder.ToString();
        }
    }
}
