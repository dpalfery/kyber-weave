package fixture

import "fmt"

// Add returns the sum of two integers.
func Add(left, right int) int {
	return left + right
}

// Describe returns a text form of the sum.
func Describe(left, right int) string {
	return fmt.Sprintf("%s", left+right)
}
